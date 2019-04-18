# Jellyfin plugin for Windows MediaCenter with ServerWMC

## Features
- Stream LiveTV
- Stream Recorded Programs
- Program Guide
- Manage Recordings

## Prerequisites
- [Jellyfin](https://jellyfin.github.io/)
- Windows MediaCenter
- [ServerWMC](https://serverwmc.github.io/)

## Installation
- Jellyfin -> Plugins -> ServerWMC

## Build Instructions
```
dotnet publish --configuration Release --output bin
```

Special thanks to **KrustyReturns** for initial development and permission to port this plugin to Jellyfin