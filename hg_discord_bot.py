import asyncio
import base64
import json
import os
import re
import shutil
import time
import uuid
from io import BytesIO
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import discord
from discord import app_commands

try:
    from PIL import Image, ImageDraw, ImageFont
except Exception:
    Image = ImageDraw = ImageFont = None

VERSION = "9.10.0"
BOT_TOKEN = os.environ.get("HG_DISCORD_BOT_TOKEN", "").strip()
CHANNEL_ID = int(os.environ.get("HG_DISCORD_CHANNEL_ID", "0") or "0")
ADMIN_USER_ID = int(os.environ.get("HG_DISCORD_ADMIN_ID", "0") or "0")
DATA_ROOT = Path(os.environ.get("HG_DATA_ROOT", "/mnt/sda1/HGMultiplayer"))
BRIDGE_ROOT = DATA_ROOT / "bridge"
INBOX = BRIDGE_ROOT / "discord_inbox.tsv"
OUTBOX = BRIDGE_ROOT / "discord_outbox.tsv"
STATUS = BRIDGE_ROOT / "server_status.tsv"
OFFSET = BRIDGE_ROOT / "discord_outbox.offset"
MESSAGE_MAP = BRIDGE_ROOT / "discord_message_places.json"
MAP_RESULTS = BRIDGE_ROOT / "map_results"
MAP_TIMEOUT_SECONDS = max(5.0, float(os.environ.get("HG_MAP_TIMEOUT_SECONDS", "25") or "25"))
MAP_WIDTH = max(640, min(1920, int(os.environ.get("HG_MAP_WIDTH", "1280") or "1280")))
MAP_HEIGHT = max(360, min(1080, int(os.environ.get("HG_MAP_HEIGHT", "720") or "720")))
MAP_MAX_BYTES = 24 * 1024 * 1024
MAP_CAST_ROOT = BRIDGE_ROOT / "map_cast"
MAPCAST_WIDTH = max(640, min(1280, int(os.environ.get("HG_MAPCAST_WIDTH", "854") or "854")))
MAPCAST_HEIGHT = max(360, min(720, int(os.environ.get("HG_MAPCAST_HEIGHT", "480") or "480")))
MAPCAST_CAPTURE_FPS = max(2, min(10, int(os.environ.get("HG_MAPCAST_CAPTURE_FPS", "5") or "5")))
MAPCAST_OUTPUT_FPS = max(15, min(60, int(os.environ.get("HG_MAPCAST_OUTPUT_FPS", "30") or "30")))
MAPCAST_JPEG_QUALITY = max(35, min(80, int(os.environ.get("HG_MAPCAST_JPEG_QUALITY", "52") or "52")))

COMMAND_SUGGESTIONS = [
    "players", "ping", "help", "heal", "revive", "tp", "give", "xp",
    "addxp", "god", "invincible", "spectate", "kill", "builder", "fly", "noclip",
    "admin", "unadmin"
]

def b64e(text: str) -> str:
    return base64.b64encode((text or "").encode("utf-8")).decode("ascii")

def b64d(text: str) -> str:
    try:
        return base64.b64decode((text or "").encode("ascii")).decode("utf-8", "replace")
    except Exception:
        return ""

def append_inbox(parts: List[str]) -> None:
    BRIDGE_ROOT.mkdir(parents=True, exist_ok=True)
    with INBOX.open("a", encoding="utf-8") as f:
        f.write("\t".join(parts) + "\n")
        f.flush()
        os.fsync(f.fileno())

def read_offset() -> int:
    try:
        return max(0, int(OFFSET.read_text(encoding="utf-8").strip()))
    except Exception:
        current_end = OUTBOX.stat().st_size if OUTBOX.exists() else 0
        write_offset(current_end)
        return current_end

def write_offset(value: int) -> None:
    tmp = OFFSET.with_suffix(".tmp")
    with tmp.open("w", encoding="utf-8") as f:
        f.write(str(max(0, int(value))))
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, OFFSET)

def load_message_map() -> Dict[str, str]:
    try:
        raw = json.loads(MESSAGE_MAP.read_text(encoding="utf-8"))
        return {str(k): str(v) for k, v in raw.items()}
    except Exception:
        return {}

def save_message_map(mapping: Dict[str, str]) -> None:

    items = list(mapping.items())[-1500:]
    trimmed = dict(items)
    try:
        tmp = MESSAGE_MAP.with_suffix(".tmp")
        tmp.write_text(json.dumps(trimmed, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, MESSAGE_MAP)
    except Exception:
        pass

def parse_status() -> dict:
    result = {"online": False, "places": {}, "players": {}}
    try:
        lines = STATUS.read_text(encoding="utf-8", errors="replace").splitlines()
    except Exception:
        return result
    for line in lines:
        p = line.split("\t")
        if not p:
            continue
        if p[0] == "server" and len(p) >= 3:
            result["online"] = p[2] == "1"
        elif p[0] == "place" and len(p) >= 5:
            key = p[1]
            result["places"][key] = {
                "key": key,
                "label": b64d(p[2]) or key,
                "count": int(p[3]) if p[3].isdigit() else 0,
                "mode": int(p[4]) if p[4].isdigit() else 0,
                "suspended": int(p[5]) if len(p) >= 6 and p[5].isdigit() else 0,
            }
        elif p[0] == "player" and len(p) >= 5:
            place = p[1]
            try:
                ping = int(p[4])
            except Exception:
                ping = -1
            result["players"].setdefault(place, []).append({
                "id": p[2],
                "name": b64d(p[3]) or "Player",
                "ping": ping,
                "admin": len(p) >= 6 and p[5] == "1",
                "uuid": p[6] if len(p) >= 7 else "",
                "suspended": len(p) >= 8 and p[7] == "1",
            })
    return result

def place_choices() -> List[Tuple[str, str]]:
    status = parse_status()
    choices: List[Tuple[str, str]] = []
    for key, info in status["places"].items():

        if info["count"] <= 0:
            continue
        label = f'{info["label"]} ({info["count"]})'
        choices.append((label[:100], key[:100]))
    return choices[:25]

def pretty_place(place_key: str, fallback: str = "") -> str:
    if place_key == "plaza":
        return "Plaza"
    if place_key == "exploration":
        return "Exploration"
    if place_key.startswith("room:"):
        return place_key[5:]
    return fallback or place_key or "Room"

def active_players_for_place(place: str) -> List[dict]:
    return [p for p in parse_status()["players"].get(place, []) if not p.get("suspended")]

def safe_map_result_path(value: str) -> Optional[Path]:

    try:
        root = MAP_RESULTS.resolve()
        raw = Path(value)
        candidate = raw if raw.is_absolute() else MAP_RESULTS / raw
        candidate = candidate.resolve()
        if candidate == root or root not in candidate.parents:
            return None
        return candidate
    except Exception:
        return None

def _parse_color(value: Any, default=(80, 80, 80, 255)):
    if isinstance(value, (list, tuple)) and len(value) in (3, 4):
        try:
            vals = [max(0, min(255, int(v))) for v in value]
            if len(vals) == 3:
                vals.append(255)
            return tuple(vals)
        except Exception:
            return default
    if isinstance(value, str):
        v = value.strip().lstrip("#")
        if len(v) in (6, 8):
            try:
                vals = tuple(int(v[i:i + 2], 16) for i in range(0, len(v), 2))
                return vals if len(vals) == 4 else vals + (255,)
            except Exception:
                pass
    return default

def render_scene_json(scene: dict) -> Optional[bytes]:

    if Image is None or ImageDraw is None:
        return None
    try:
        width = max(320, min(1920, int(scene.get("width", MAP_WIDTH))))
        height = max(180, min(1080, int(scene.get("height", MAP_HEIGHT))))
        bg = _parse_color(scene.get("background"), (20, 24, 28, 255))
        image = Image.new("RGBA", (width, height), bg)
        draw = ImageDraw.Draw(image, "RGBA")

        camera = scene.get("camera") if isinstance(scene.get("camera"), dict) else {}
        cx = float(camera.get("x", 0.0))
        cy = float(camera.get("y", 0.0))
        world_width = max(0.01, float(camera.get("world_width", 32.0)))
        world_height = max(0.01, float(camera.get("world_height", world_width * height / width)))

        def screen_xy(x: float, y: float):
            sx = (float(x) - (cx - world_width / 2.0)) / world_width * width
            sy = height - ((float(y) - (cy - world_height / 2.0)) / world_height * height)
            return sx, sy

        objects = scene.get("objects", [])
        if not isinstance(objects, list):
            objects = []
        priority = {"background": 0, "block": 1, "element": 2, "item": 3, "enemy": 4, "player": 5, "effect": 6}
        objects = sorted((o for o in objects if isinstance(o, dict)), key=lambda o: priority.get(str(o.get("type", "element")), 2))
        default_colors = {
            "block": (100, 105, 110, 255), "element": (115, 150, 105, 255),
            "item": (220, 190, 80, 255), "enemy": (205, 75, 75, 255),
            "player": (90, 170, 235, 255), "effect": (245, 245, 245, 150),
            "background": (45, 50, 55, 255),
        }
        for obj in objects[:12000]:
            typ = str(obj.get("type", "element")).lower()
            x = float(obj.get("x", 0.0)); y = float(obj.get("y", 0.0))
            w = max(0.05, float(obj.get("w", obj.get("width", 0.8))))
            h = max(0.05, float(obj.get("h", obj.get("height", 0.8))))
            sx, sy = screen_xy(x, y)
            px_w = max(2.0, w / world_width * width)
            px_h = max(2.0, h / world_height * height)
            color = _parse_color(obj.get("color"), default_colors.get(typ, (160, 160, 160, 255)))
            box = (sx - px_w / 2, sy - px_h / 2, sx + px_w / 2, sy + px_h / 2)
            if typ in ("player", "enemy"):
                draw.ellipse(box, fill=color, outline=(10, 10, 10, 230), width=max(1, int(min(px_w, px_h) * 0.07)))
            else:
                draw.rectangle(box, fill=color, outline=(10, 10, 10, 160), width=1)
            label = str(obj.get("label", "")).strip()
            if label and typ in ("player", "enemy"):
                draw.text((sx + 4, sy - px_h / 2 - 14), label[:32], fill=(255, 255, 255, 235))

        draw.line((width / 2 - 8, height / 2, width / 2 + 8, height / 2), fill=(255, 255, 255, 180), width=1)
        draw.line((width / 2, height / 2 - 8, width / 2, height / 2 + 8), fill=(255, 255, 255, 180), width=1)
        out = BytesIO()
        image.convert("RGB").save(out, format="PNG", optimize=True)
        return out.getvalue()
    except Exception as exc:
        print(f"[HG Discord] scene JSON render failed: {exc}")
        return None

async def wait_for_map_result(request_id: str, timeout: float) -> Tuple[str, Optional[bytes], str]:

    MAP_RESULTS.mkdir(parents=True, exist_ok=True)
    MAP_CAST_ROOT.mkdir(parents=True, exist_ok=True)
    png_path = MAP_RESULTS / f"{request_id}.png"
    jpg_path = MAP_RESULTS / f"{request_id}.jpg"
    jpeg_path = MAP_RESULTS / f"{request_id}.jpeg"
    json_path = MAP_RESULTS / f"{request_id}.json"
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            if png_path.exists() and png_path.is_file():
                if png_path.stat().st_size > MAP_MAX_BYTES:
                    return "error", None, "Capture image is too large."
                data = png_path.read_bytes()
                try:
                    png_path.unlink()
                except Exception:
                    pass
                if data.startswith(b"\x89PNG\r\n\x1a\n"):
                    return "ok", data, ""
                return "error", None, "Capture provider returned an invalid PNG."

            for image_path in (jpg_path, jpeg_path):
                if image_path.exists() and image_path.is_file():
                    if image_path.stat().st_size > MAP_MAX_BYTES:
                        return "error", None, "Capture image is too large."
                    data = image_path.read_bytes()
                    try:
                        image_path.unlink()
                    except Exception:
                        pass
                    if data.startswith(b"\xff\xd8\xff"):
                        return "ok", data, ""
                    return "error", None, "Capture provider returned an invalid JPEG."

            if json_path.exists() and json_path.is_file():
                raw = json.loads(json_path.read_text(encoding="utf-8", errors="replace"))
                try:
                    json_path.unlink()
                except Exception:
                    pass
                status = str(raw.get("status", "ok")).lower()
                if status != "ok":
                    return "error", None, str(raw.get("message", "Map capture failed."))[:500]
                image_value = str(raw.get("image", "") or "")
                if image_value:
                    image_path = safe_map_result_path(image_value)
                    if image_path is None or not image_path.exists() or not image_path.is_file():
                        return "error", None, "Capture provider returned an unsafe or missing image path."
                    if image_path.stat().st_size > MAP_MAX_BYTES:
                        return "error", None, "Capture image is too large."
                    data = image_path.read_bytes()
                    try:
                        image_path.unlink()
                    except Exception:
                        pass
                    if data.startswith(b"\x89PNG\r\n\x1a\n") or data.startswith(b"\xff\xd8\xff"):
                        return "ok", data, ""
                    return "error", None, "Capture provider image is neither PNG nor JPEG."
                scene = raw.get("scene")
                if isinstance(scene, dict):
                    data = render_scene_json(scene)
                    if data:
                        return "ok", data, ""
                    return "error", None, "Scene JSON was returned, but Pillow is not installed on the bot host."
                return "error", None, "Capture response contained neither an image nor a scene."
        except Exception as exc:
            return "error", None, f"Failed to read capture response: {exc}"
        await asyncio.sleep(0.25)
    return "timeout", None, ""

class MapCastSession:
    def __init__(self, cast_id: str, place: str, process: asyncio.subprocess.Process, redactions: Optional[List[str]] = None) -> None:
        self.cast_id = cast_id
        self.place = place
        self.process = process
        self.task: Optional[asyncio.Task] = None
        self.stderr_task: Optional[asyncio.Task] = None
        self.last_frame: Optional[bytes] = None
        self.last_mtime_ns = -1
        self.last_ffmpeg_error = ""
        self.stopping = False
        self.redactions = redactions or []

def _build_stream_target(stream_url: str, stream_key: str) -> str:
    url = (stream_url or "").strip()
    key = (stream_key or "").strip()
    if "{stream_key}" in url:
        return url.replace("{stream_key}", key)
    return url.rstrip("/") + "/" + key

def _valid_stream_url(value: str) -> bool:
    v = (value or "").strip().lower()
    return v.startswith("rtmp://") or v.startswith("rtmps://")

def _redact_ffmpeg_line(value: str, secrets: Optional[List[str]] = None) -> str:
    text = (value or "").replace("\r", " ").replace("\n", " ").strip()
    for secret in sorted((s for s in (secrets or []) if s), key=len, reverse=True):
        text = text.replace(secret, "[REDACTED]")
    text = re.sub(r"(?i)rtmps?://[^\s<>\"']+", "[RTMP_TARGET_REDACTED]", text)
    return text[:1000]


async def _spawn_mapcast_ffmpeg(stream_url: str, stream_key: str) -> Tuple[Optional[asyncio.subprocess.Process], str]:
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        return None, "FFmpeg is not installed on the bot host."
    target = _build_stream_target(stream_url, stream_key)

    args = [
        ffmpeg, "-hide_banner", "-loglevel", "warning",
        "-f", "mjpeg", "-framerate", str(MAPCAST_CAPTURE_FPS), "-i", "pipe:0",

        "-re", "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=44100",
        "-map", "0:v:0", "-map", "1:a:0",
        "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
        "-pix_fmt", "yuv420p", "-r", str(MAPCAST_OUTPUT_FPS),
        "-g", str(MAPCAST_OUTPUT_FPS * 2), "-keyint_min", str(MAPCAST_OUTPUT_FPS * 2), "-sc_threshold", "0",

        "-b:v", "4000k", "-minrate", "4000k", "-maxrate", "4000k", "-bufsize", "8000k",
        "-c:a", "aac", "-b:a", "128k", "-ar", "44100", "-ac", "2",
        "-f", "flv", target,
    ]
    try:
        proc = await asyncio.create_subprocess_exec(
            *args,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.DEVNULL,
            stderr=asyncio.subprocess.PIPE,
        )
        print(f"[HG MapCast] FFmpeg spawned pid={proc.pid} input=mjpeg {MAPCAST_CAPTURE_FPS}fps output=h264+aac {MAPCAST_OUTPUT_FPS}fps", flush=True)
        return proc, ""
    except Exception as exc:
        return None, f"Could not start FFmpeg: {type(exc).__name__}: {exc}"

class HGDiscordBridge(discord.Client):
    def __init__(self) -> None:
        intents = discord.Intents.default()
        intents.message_content = True
        super().__init__(intents=intents, status=discord.Status.dnd, allowed_mentions=discord.AllowedMentions.none())
        self.tree = app_commands.CommandTree(self)
        self.message_places = load_message_map()
        self.channel: Optional[discord.abc.Messageable] = None
        self.presence_flip = False
        self.mapcasts: Dict[str, MapCastSession] = {}
        self._register_commands()

    async def setup_hook(self) -> None:

        append_inbox(["MAPCAST", "STOPALL", "bot-startup", "*"])
        await self.tree.sync()
        self.outbox_task = asyncio.create_task(self.outbox_loop())
        self.presence_task = asyncio.create_task(self.presence_loop())

    async def on_ready(self) -> None:
        try:
            self.channel = self.get_channel(CHANNEL_ID) or await self.fetch_channel(CHANNEL_ID)
        except Exception as exc:
            print(f"[HG Discord] channel lookup failed: {exc}")
            self.channel = None
        print(f"[HG Discord] v{VERSION} logged in as {self.user} · channel={CHANNEL_ID}")

    async def on_message(self, message: discord.Message) -> None:
        if message.author.bot or message.channel.id != CHANNEL_ID:
            return
        text = (message.content or "").strip()
        if not text:
            return

        target = "*"
        if message.reference and message.reference.message_id:
            target = self.message_places.get(str(message.reference.message_id), "*")
        author = message.author.display_name or message.author.name
        append_inbox(["CHAT", target, b64e(author), b64e(text[:240])])

    def _register_commands(self) -> None:
        @self.tree.command(name="info", description="Show HG server, players, rooms and latency.")
        async def info(interaction: discord.Interaction) -> None:
            status = parse_status()
            lines = [
                f"Server: {'Online' if status['online'] else 'Offline'}",
                f"Bot latency: {round(self.latency * 1000)} ms",
            ]
            places = status["places"]
            plaza = places.get("plaza", {"count": 0})
            exploration = places.get("exploration", {"count": 0})
            lines.append(f"Plaza: {plaza.get('count', 0)} active player(s)")
            lines.append(f"Exploration: {exploration.get('count', 0)} active player(s)")

            room_infos = [v for k, v in places.items() if k.startswith("room:") and v.get("count", 0) > 0]
            lines.append(f"Internet rooms: {len(room_infos)}")
            for room in room_infos[:12]:
                lines.append(f"- {room['label']}: {room['count']} active player(s)")

            player_rows = []
            for place, players in status["players"].items():
                for p in players:
                    if p.get("suspended"):
                        continue
                    ping = "?" if p["ping"] < 0 else f'{p["ping"]} ms'
                    admin_tag = " · ADMIN" if place == "exploration" and p.get("admin") else ""
                    player_rows.append(f"- [{pretty_place(place)}] {p['name']} · {ping}{admin_tag}")
            if player_rows:
                lines.append("Active players:")
                lines.extend(player_rows[:30])
            else:
                lines.append("Active players: none")

            suspended_total = sum(v.get("suspended", 0) for v in places.values())
            if suspended_total:
                lines.append(f"Suspended cache: {suspended_total} session(s) · auto-expires")
            await interaction.response.send_message("\n".join(lines)[:1900])

        @self.tree.command(name="expreset", description="Reset the persistent Exploration map.")
        async def expreset(interaction: discord.Interaction) -> None:
            if interaction.user.id != ADMIN_USER_ID:
                await interaction.response.send_message("You are not allowed to use this command.", ephemeral=True)
                return
            append_inbox(["EXPRESET"])
            await interaction.response.send_message("Exploration world reset was requested.", ephemeral=True)

        @self.tree.command(name="expadmin", description="Grant or revoke persistent Exploration admin permission.")
        @app_commands.describe(player="Connected Exploration player", action="Grant or revoke admin")
        @app_commands.choices(action=[
            app_commands.Choice(name="Grant", value="grant"),
            app_commands.Choice(name="Revoke", value="revoke"),
        ])
        async def expadmin(interaction: discord.Interaction, player: str, action: app_commands.Choice[str]) -> None:
            if interaction.user.id != ADMIN_USER_ID:
                await interaction.response.send_message("You are not allowed to use this command.", ephemeral=True)
                return
            status = parse_status()
            exploration_players = {str(p["id"]): p for p in status["players"].get("exploration", []) if not p.get("suspended")}
            target = exploration_players.get(str(player))
            if target is None:
                await interaction.response.send_message("That player is no longer connected to Exploration.", ephemeral=True)
                return
            verb = action.value if action and action.value in {"grant", "revoke"} else ""
            if not verb:
                await interaction.response.send_message("Invalid admin action.", ephemeral=True)
                return
            append_inbox(["EXPADMIN", str(player), verb])
            await interaction.response.send_message(
                f"Persistent Exploration admin {verb} requested for {target['name']}.",
                ephemeral=True,
            )

        @expadmin.autocomplete("player")
        async def expadmin_player_autocomplete(interaction: discord.Interaction, current: str):
            current_l = (current or "").lower()
            status = parse_status()
            out = []
            for p in status["players"].get("exploration", []):
                if p.get("suspended"):
                    continue
                state = "ADMIN" if p.get("admin") else "PLAYER"
                label = f"{p['name']} · #{p['id']} · {state}"
                if current_l and current_l not in label.lower():
                    continue
                out.append(app_commands.Choice(name=label[:100], value=str(p["id"])))
            return out[:25]

        @self.tree.command(name="cmd", description="Run a game command in a running place.")
        @app_commands.describe(place="Running place", command="Game command")
        async def cmd(interaction: discord.Interaction, place: str, command: str) -> None:
            if interaction.user.id != ADMIN_USER_ID:
                await interaction.response.send_message("You are not allowed to use this command.", ephemeral=True)
                return
            command = (command or "").strip().lstrip("/")
            if not command:
                await interaction.response.send_message("Command cannot be empty.", ephemeral=True)
                return
            valid = {value for _, value in place_choices()}
            if place not in valid:
                await interaction.response.send_message("That place is no longer running.", ephemeral=True)
                return
            append_inbox(["CMD", place, b64e(command[:300])])
            await interaction.response.send_message(
                f"Command sent to {pretty_place(place)}: /{command}",
                ephemeral=True,
            )

        @cmd.autocomplete("place")
        async def cmd_place_autocomplete(interaction: discord.Interaction, current: str):
            current_l = (current or "").lower()
            out = []
            for name, value in place_choices():
                if current_l and current_l not in name.lower() and current_l not in value.lower():
                    continue
                out.append(app_commands.Choice(name=name, value=value))
            return out[:25]

        @cmd.autocomplete("command")
        async def cmd_command_autocomplete(interaction: discord.Interaction, current: str):
            current_l = (current or "").lower().lstrip("/")
            choices = []
            for value in COMMAND_SUGGESTIONS:
                if current_l and current_l not in value.lower():
                    continue
                choices.append(app_commands.Choice(name="/" + value, value=value))
            return choices[:25]

        @self.tree.command(name="map", description="Capture a room from a connected player's viewpoint.")
        @app_commands.describe(place="Currently running room", player="Active player in that room")
        async def map_command(interaction: discord.Interaction, place: str, player: str) -> None:
            status = parse_status()
            info = status["places"].get(place)
            if info is None or info.get("count", 0) <= 0:
                await interaction.response.send_message("That room is no longer running.", ephemeral=True)
                return
            target = None
            for p in status["players"].get(place, []):
                if not p.get("suspended") and str(p.get("id")) == str(player):
                    target = p
                    break
            if target is None:
                await interaction.response.send_message("That player is no longer active in that room.", ephemeral=True)
                return

            request_id = uuid.uuid4().hex

            append_inbox([
                "MAP", request_id, place, str(target["id"]),
                str(MAP_WIDTH), str(MAP_HEIGHT), str(interaction.user.id),
            ])
            await interaction.response.defer(thinking=True)
            result, image_bytes, error = await wait_for_map_result(request_id, MAP_TIMEOUT_SECONDS)
            if result == "ok" and image_bytes:
                ext = ".jpg" if image_bytes.startswith(b"\xff\xd8\xff") else ".png"
                filename = f"HG_map_{pretty_place(place).replace(' ', '_')}_{target['name'][:24].replace(' ', '_')}{ext}"
                file = discord.File(BytesIO(image_bytes), filename=filename)
                embed = discord.Embed(
                    title=f"{pretty_place(place)} · {target['name']}",
                    description="Player-view map capture",
                    color=0x5865F2,
                )
                embed.set_image(url=f"attachment://{filename}")
                await interaction.followup.send(embed=embed, file=file)
                return
            if result == "error":
                await interaction.followup.send(f"Map capture failed: {error}", ephemeral=True)
                return
            await interaction.followup.send(
                "Map capture request timed out. The selected game client may be backgrounded, too old, or still loading the scene.",
                ephemeral=True,
            )

        @map_command.autocomplete("place")
        async def map_place_autocomplete(interaction: discord.Interaction, current: str):
            current_l = (current or "").lower()
            out = []
            for name, value in place_choices():
                if current_l and current_l not in name.lower() and current_l not in value.lower():
                    continue
                out.append(app_commands.Choice(name=name, value=value))
            return out[:25]

        @map_command.autocomplete("player")
        async def map_player_autocomplete(interaction: discord.Interaction, current: str):
            current_l = (current or "").lower()
            try:
                place = str(getattr(interaction.namespace, "place", "") or "")
            except Exception:
                place = ""
            if not place:
                return []
            out = []
            for p in active_players_for_place(place):
                label = f"{p['name']} · #{p['id']}"
                if current_l and current_l not in label.lower():
                    continue
                out.append(app_commands.Choice(name=label[:100], value=str(p["id"])))
            return out[:25]

        @self.tree.command(name="mapcast", description="Broadcast a running multiplayer place to an RTMP/YouTube stream.")
        @app_commands.describe(place="Running online multiplayer place", stream_key="YouTube/RTMP stream key", stream_url="RTMP/RTMPS stream URL")
        async def mapcast(interaction: discord.Interaction, place: str, stream_key: str, stream_url: str) -> None:
            if interaction.user.id != ADMIN_USER_ID:
                await interaction.response.send_message("You are not allowed to use this command.", ephemeral=True)
                return
            status = parse_status()
            info = status["places"].get(place)
            if info is None or info.get("count", 0) <= 0:
                await interaction.response.send_message("That place is no longer running.", ephemeral=True)
                return
            if not _valid_stream_url(stream_url):
                await interaction.response.send_message("Stream URL must start with rtmp:// or rtmps://.", ephemeral=True)
                return
            if not (stream_key or "").strip() or len(stream_key) > 256:
                await interaction.response.send_message("Invalid stream key.", ephemeral=True)
                return

            await interaction.response.defer(ephemeral=True, thinking=True)
            await self.stop_mapcast(place, notify_server=True)

            cast_id = uuid.uuid4().hex
            cast_dir = MAP_CAST_ROOT / cast_id
            cast_dir.mkdir(parents=True, exist_ok=True)
            latest = cast_dir / "latest.jpg"
            error_file = cast_dir / "error.txt"
            append_inbox([
                "MAPCAST", "START", cast_id, place,
                str(MAPCAST_WIDTH), str(MAPCAST_HEIGHT), str(MAPCAST_CAPTURE_FPS), str(MAPCAST_JPEG_QUALITY),
            ])
            print(f"[HG MapCast] capture requested cast={cast_id} place={place}", flush=True)

            ready_deadline = time.monotonic() + 10.0
            error_text = ""
            first_frame: Optional[bytes] = None
            while time.monotonic() < ready_deadline:
                if error_file.exists():
                    try:
                        error_text = error_file.read_text(encoding="utf-8", errors="replace")[:500]
                    except Exception:
                        error_text = "Game capture provider rejected the broadcast."
                    break
                try:
                    if latest.exists() and latest.is_file() and 0 < latest.stat().st_size <= MAP_MAX_BYTES:
                        data = latest.read_bytes()
                        if data.startswith(b"\xff\xd8\xff"):
                            first_frame = data
                            break
                except Exception:
                    pass
                await asyncio.sleep(0.15)

            if not first_frame:
                append_inbox(["MAPCAST", "STOP", cast_id, place])
                try:
                    for child in cast_dir.iterdir():
                        if child.is_file(): child.unlink()
                    cast_dir.rmdir()
                except Exception:
                    pass
                await interaction.followup.send(error_text or "No valid game frame arrived for /mapcast.", ephemeral=True)
                return

            proc, spawn_error = await _spawn_mapcast_ffmpeg(stream_url, stream_key)

            if proc is None:
                stream_key = ""
                append_inbox(["MAPCAST", "STOP", cast_id, place])
                await interaction.followup.send(spawn_error or "Could not start FFmpeg.", ephemeral=True)
                return

            session = MapCastSession(cast_id, place, proc, [stream_url, _build_stream_target(stream_url, stream_key), stream_key])
            stream_key = ""
            session.last_frame = first_frame
            try:
                session.last_mtime_ns = latest.stat().st_mtime_ns
            except Exception:
                session.last_mtime_ns = -1
            self.mapcasts[place] = session
            session.stderr_task = asyncio.create_task(self.mapcast_ffmpeg_logger(session))
            session.task = asyncio.create_task(self.mapcast_feeder(session))

            await asyncio.sleep(1.5)
            if proc.returncode is not None:
                error_text = session.last_ffmpeg_error or f"FFmpeg exited with code {proc.returncode}."
                await self.stop_mapcast(place, notify_server=True)
                await interaction.followup.send(f"MapCast FFmpeg failed: {error_text}", ephemeral=True)
                return

            print(f"[HG MapCast] live cast={cast_id} place={place} ffmpeg_pid={proc.pid}", flush=True)
            await interaction.followup.send(
                f"Map broadcast started for {pretty_place(place)} · {MAPCAST_WIDTH}×{MAPCAST_HEIGHT} capture @ {MAPCAST_CAPTURE_FPS} fps → {MAPCAST_OUTPUT_FPS} fps H.264 + silent AAC.",
                ephemeral=True,
            )
        @mapcast.autocomplete("place")
        async def mapcast_place_autocomplete(interaction: discord.Interaction, current: str):
            current_l = (current or "").lower()
            out = []
            for name, value in place_choices():
                if current_l and current_l not in name.lower() and current_l not in value.lower():
                    continue
                out.append(app_commands.Choice(name=name, value=value))
            return out[:25]

        @self.tree.command(name="mapcaststop", description="Stop an active /mapcast broadcast.")
        @app_commands.describe(place="Place whose broadcast should stop")
        async def mapcaststop(interaction: discord.Interaction, place: str) -> None:
            if interaction.user.id != ADMIN_USER_ID:
                await interaction.response.send_message("You are not allowed to use this command.", ephemeral=True)
                return
            if place not in self.mapcasts:
                await interaction.response.send_message("That place is not being broadcast by this bot.", ephemeral=True)
                return
            await self.stop_mapcast(place, notify_server=True)
            await interaction.response.send_message(f"Map broadcast stopped for {pretty_place(place)}.", ephemeral=True)

        @mapcaststop.autocomplete("place")
        async def mapcaststop_place_autocomplete(interaction: discord.Interaction, current: str):
            current_l = (current or "").lower()
            out = []
            for place in sorted(self.mapcasts.keys()):
                label = pretty_place(place)
                if current_l and current_l not in label.lower() and current_l not in place.lower():
                    continue
                out.append(app_commands.Choice(name=label[:100], value=place[:100]))
            return out[:25]

    async def mapcast_ffmpeg_logger(self, session: MapCastSession) -> None:
        stderr = session.process.stderr
        if stderr is None:
            return
        try:
            while True:
                raw = await stderr.readline()
                if not raw:
                    break
                clean = _redact_ffmpeg_line(raw.decode("utf-8", errors="replace"), session.redactions)
                if not clean:
                    continue
                session.last_ffmpeg_error = clean
                print(f"[HG MapCast/FFmpeg] {clean}", flush=True)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            print(f"[HG MapCast] stderr reader error: {type(exc).__name__}: {exc}", flush=True)

    async def mapcast_feeder(self, session: MapCastSession) -> None:
        latest = MAP_CAST_ROOT / session.cast_id / "latest.jpg"
        error_file = MAP_CAST_ROOT / session.cast_id / "error.txt"
        interval = 1.0 / float(MAPCAST_CAPTURE_FPS)
        status_check = 0.0
        try:
            while not self.is_closed() and not session.stopping and session.process.returncode is None:
                now = time.monotonic()
                if now >= status_check:
                    status_check = now + 2.0
                    info = parse_status()["places"].get(session.place)
                    if info is None or info.get("count", 0) <= 0:
                        session.last_ffmpeg_error = "The multiplayer place is no longer active."
                        break
                try:
                    if error_file.exists() and error_file.is_file():
                        session.last_ffmpeg_error = error_file.read_text(encoding="utf-8", errors="replace")[:500]
                        break
                    if latest.exists() and latest.is_file():
                        st = latest.stat()
                        if st.st_mtime_ns != session.last_mtime_ns and 0 < st.st_size <= MAP_MAX_BYTES:
                            data = latest.read_bytes()
                            if data.startswith(b"\xff\xd8\xff"):
                                session.last_frame = data
                                session.last_mtime_ns = st.st_mtime_ns
                except Exception as exc:
                    print(f"[HG MapCast] frame read warning: {type(exc).__name__}: {exc}", flush=True)

                if session.last_frame and session.process.stdin is not None:
                    try:
                        session.process.stdin.write(session.last_frame)
                        await session.process.stdin.drain()
                    except (BrokenPipeError, ConnectionResetError):
                        break
                    except Exception as exc:
                        session.last_ffmpeg_error = f"FFmpeg stdin failed: {type(exc).__name__}: {exc}"
                        break
                await asyncio.sleep(interval)
        except asyncio.CancelledError:
            raise
        finally:
            if self.mapcasts.get(session.place) is session:
                code = session.process.returncode
                if code is not None:
                    print(f"[HG MapCast] FFmpeg exited cast={session.cast_id} code={code} reason={session.last_ffmpeg_error or 'unknown'}", flush=True)
                await self.stop_mapcast(session.place, notify_server=True, from_feeder=True)
    async def stop_mapcast(self, place: str, notify_server: bool, from_feeder: bool = False) -> None:
        session = self.mapcasts.pop(place, None)
        if session is None:
            return
        session.stopping = True
        if notify_server:
            append_inbox(["MAPCAST", "STOP", session.cast_id, session.place])
        current = asyncio.current_task()
        if session.task is not None and not from_feeder and session.task is not current:
            session.task.cancel()
        proc = session.process
        try:
            if proc.stdin is not None:
                proc.stdin.close()
        except Exception:
            pass
        try:
            if proc.returncode is None:
                proc.terminate()
                try:
                    await asyncio.wait_for(proc.wait(), timeout=2.0)
                except asyncio.TimeoutError:
                    proc.kill()
                    await proc.wait()
        except ProcessLookupError:
            pass
        except Exception as exc:
            print(f"[HG MapCast] FFmpeg stop warning: {type(exc).__name__}: {exc}", flush=True)
        if session.stderr_task is not None and session.stderr_task is not current:
            try:
                await asyncio.wait_for(session.stderr_task, timeout=0.5)
            except asyncio.TimeoutError:
                session.stderr_task.cancel()
            except asyncio.CancelledError:
                pass
            except Exception:
                pass
        try:
            cast_dir = MAP_CAST_ROOT / session.cast_id
            for child in cast_dir.iterdir():
                if child.is_file():
                    child.unlink()
            cast_dir.rmdir()
        except Exception:
            pass
        session.redactions = []
        print(f"[HG MapCast] stopped cast={session.cast_id} place={session.place}", flush=True)
    async def close(self) -> None:
        for place in list(self.mapcasts.keys()):
            await self.stop_mapcast(place, notify_server=True)
        await super().close()

    async def presence_loop(self) -> None:
        await self.wait_until_ready()
        while not self.is_closed():
            try:
                status = parse_status()
                plaza = status["places"].get("plaza", {}).get("count", 0)
                room_count = sum(
                    1 for k, v in status["places"].items()
                    if k.startswith("room:") and v.get("count", 0) > 0
                )

                name = f"Plaza {plaza} players" if not self.presence_flip else f"{room_count} Internet rooms running"
                self.presence_flip = not self.presence_flip
                activity = discord.Activity(type=discord.ActivityType.competing, name=name)
                await self.change_presence(status=discord.Status.dnd, activity=activity)
            except Exception as exc:
                print(f"[HG Discord] presence error: {exc}")
            await asyncio.sleep(5)

    async def outbox_loop(self) -> None:
        await self.wait_until_ready()
        offset = read_offset()
        while not self.is_closed():
            try:
                if self.channel is None:
                    await asyncio.sleep(1)
                    continue
                if not OUTBOX.exists():
                    await asyncio.sleep(1)
                    continue
                size = OUTBOX.stat().st_size
                if offset > size:
                    offset = size
                    write_offset(offset)
                with OUTBOX.open("rb") as f:
                    f.seek(offset)
                    for _ in range(100):
                        raw = f.readline()
                        if not raw or not raw.endswith(b"\n"):
                            break
                        new_offset = f.tell()
                        await self.send_outbox_line(raw.decode("utf-8", errors="replace"))
                        offset = new_offset
                        write_offset(offset)
            except Exception as exc:
                print(f"[HG Discord] outbox error: {exc}")
            await asyncio.sleep(0.6)

    async def send_outbox_line(self, raw: str) -> None:
        p = raw.rstrip("\r\n").split("\t")
        if len(p) < 6 or self.channel is None:
            return
        _timestamp, kind, place_key, place_label_b64, author_b64, text_b64 = p[:6]
        place_label = b64d(place_label_b64) or pretty_place(place_key)
        author = b64d(author_b64)
        text = b64d(text_b64)
        prefix = f"[{place_label}]"

        if kind == "chat":
            content = f"{prefix} {author}: {text}"
        elif kind == "speech":
            content = f"{prefix} [Speech] {author}: {text}"
        elif kind == "join":
            content = f"{prefix} {text}"
        elif kind == "leave":
            content = f"{prefix} {text}"
        elif kind == "death_report":
            cause, sep, report = text.partition("\x1f")
            if not sep:
                cause, report = "Unknown", text
            embed = discord.Embed(
                title=f"Death Report — {author}",
                description=f"**Cause of Death:** {cause or 'Unknown'}",
                color=0x6B1F2A,
            )
            clean_report = (report or "No forensic data was received.").strip()
            embed.add_field(name="Forensic Status", value=clean_report[:1024], inline=False)
            embed.set_footer(text=place_label)
            sent = await self.channel.send(embed=embed)
            self.message_places[str(sent.id)] = place_key
            save_message_map(self.message_places)
            return
        elif kind == "death":

            if place_key == "exploration":
                return
            content = f"{prefix} {text}"
        elif kind == "revive":
            content = f"{prefix} {text}"
        elif kind == "system":
            content = f"{prefix} {text}"
        elif kind == "admin_command":
            content = f"{prefix} Admin command: /{text}"
        elif kind == "revive_request":
            return
        elif kind in ("server_start", "server_stop"):
            content = f"[Server] {text}"
            place_key = "server"
        else:
            content = f"{prefix} {text}"

        sent = await self.channel.send(content[:1900])
        self.message_places[str(sent.id)] = place_key
        save_message_map(self.message_places)

def main() -> None:
    if not BOT_TOKEN:
        raise SystemExit("HG_DISCORD_BOT_TOKEN is not set.")
    if CHANNEL_ID <= 0:
        raise SystemExit("HG_DISCORD_CHANNEL_ID is not set.")
    BRIDGE_ROOT.mkdir(parents=True, exist_ok=True)
    MAP_RESULTS.mkdir(parents=True, exist_ok=True)
    INBOX.touch(exist_ok=True)
    OUTBOX.touch(exist_ok=True)
    client = HGDiscordBridge()
    client.run(BOT_TOKEN, log_handler=None)

if __name__ == "__main__":
    main()
