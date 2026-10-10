# HGCUServer

HGCUServer is a community-developed multiplayer server for **Casualties: Unknown Mobile**. It provides shared game sessions, persistent worlds, and a bridge between compatible game clients and Discord.

## Features

- **Plaza:** A shared multiplayer hub with synchronized players and world changes.
- **Exploration:** A persistent multiplayer world with player saves, death and respawn handling, and administrative controls.
- **Internet Rooms:** Player-created rooms with a room browser and session management.
- **Multiplayer Synchronization:** Relays player movement, state updates, chat, and gameplay events between connected clients.
- **World Persistence:** Stores supported world changes and player data across sessions.
- **Session Recovery:** Supports reconnection after temporary disconnects or backgrounding, where supported by the client.
- **Local Connection Bridge:** Supports compatible Bluetooth/USB gateway connections through the game's network bridge.
- **Discord Integration:** Two-way chat, player and room status, death reports, and administrator commands.
- **Map Capture and MapCast:** Supports client-provided map screenshots and FFmpeg-based RTMP streaming when a compatible capture provider is available.

## Components

- `HGPlazaServer.cs` — C# multiplayer relay server using LiteNetLib.
- `hg_discord_bot.py` — Python Discord bridge for chat, status, moderation, and media features.

HGCUServer is a server-side project, not a standalone game. A compatible Casualties: Unknown client is required to play.
