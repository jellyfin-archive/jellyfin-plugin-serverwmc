# Jellyfin plugin for Windows MediaCenter with ServerWMC
[![](https://img.shields.io/github/languages/top/prplhaz4/jellyfin-plugin-serverwmc.svg)](https://github.com/prplhaz4/jellyfin-plugin-serverwmc)
[![](https://img.shields.io/github/contributors/prplhaz4/jellyfin-plugin-serverwmc.svg)](https://github.com/prplhaz4/jellyfin-plugin-serverwmc)
[![](https://img.shields.io/github/license/prplhaz4/jellyfin-plugin-serverwmc.svg)](https://github.com/prplhaz4/jellyfin-plugin-serverwmc)

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
