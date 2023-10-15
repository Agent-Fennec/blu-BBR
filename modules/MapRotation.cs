﻿using BattleBitAPI.Common;
using BBRAPIModules;
using Bluscream;
using Commands;
using Microsoft.VisualBasic;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BattleBitBaseModules;

/// <summary>
/// Author: @RainOrigami modified by @_dx2
/// </summary>
[RequireModule(typeof(Bluscream.BluscreamLib))]
[RequireModule(typeof(GameModeRotation))]
[RequireModule(typeof(CommandHandler))]
[Module("Adds a small tweak to the map rotation so that maps that were just played take more time to appear again, this works by counting how many matches happened since the maps were last played and before getting to the voting screen, the n least played ones are picked to appear on the voting screen . It also adds a command so that any player can know what maps are in the rotation.", "1.4.3")]
public class MapRotation : BattleBitModule
{
    [ModuleReference]
    public CommandHandler CommandHandler { get; set; } = null!;
    [ModuleReference]
    public GameModeRotation GameModeRotation { get; set; } = null!;
    [ModuleReference]
    public BluscreamLib BluscreamLib { get; set; } = null!;

    public MapRotationConfiguration Configuration { get; set; } = null!;

    public override async Task OnConnected()
    {
        await Task.Delay(100);
        for (int i = Configuration.Maps.Length - 1; i >= 0; i--)
        {
            var map = FindMap(Configuration.Maps[i]);
            if (map == null)
            {
                Configuration.Maps = Configuration.Maps.Except(new string[] { Configuration.Maps[i] }).ToArray();
            }
            else
            {
                Configuration.Maps[i] = map.Name;
            }
        }

        var currentRotation = GameModeRotation.ActiveGamemodes.ConvertAll(name => name.ToLower());

        var currentMapNames = Configuration.Maps.ToList();
        var currentMaps = BluscreamLib.Maps.ToList().FindAll(map => currentMapNames.Contains(map.Name));
        var unsuportedMaps = currentMaps.FindAll(map =>
        {
            var gamemodes = map.SupportedGamemodes?.FindAll(gm => currentRotation.Contains(gm.GameMode.ToLower()));
            if (gamemodes?.Count == 0) return true;
            return gamemodes.FindIndex(gm => gm.SupportedMapSizes.Contains(Server.MapSize)) == -1;
        }).ConvertAll(map => map.Name);

        if (unsuportedMaps.Count > 0)
        {
            string outputString = "";
            foreach (var map in unsuportedMaps)
            {

                outputString += map + ", ";
            }
            this.Logger.Info(@$"{Server.ServerName}[WARNING]MapRotation: The following maps do not support the current gamemode rotation at the current mapsize: 
{outputString}, please, change the gamemodes or run the command !MapCleanup ingame to remove them");
        }

        if (Configuration.MatchesSinceSelection.Length != Configuration.Maps.Length)
        {
            ReinicializeCounters();
        }

        Configuration.Save();
        return;
    }

    public override Task OnGameStateChanged(GameState oldState, GameState newState)
    {
        if (newState == GameState.CountingDown)
        {
            if (Configuration.MatchesSinceSelection.Length != Configuration.Maps.Length)
            {
                ReinicializeCounters();
            }
            var currentMapIndex = Array.IndexOf(Configuration.Maps, Server.Map);
            if (currentMapIndex == -1)
            {
                this.Logger.Info($"{Server.ServerName} MapRotation: Current map({Server.Map}) not found in MapRotation ConfigList while reseting the counter(Did you type the name correctly?)");
            }
            else
            {
                this.Logger.Info($"{Server.ServerName} MapRotation: Starting new match in {Server.Map}");
                Configuration.MatchesSinceSelection[currentMapIndex] = 0;
            }
            var currentGamemodes = Array.ConvertAll(Server.GamemodeRotation.GetGamemodeRotation().ToArray(), gm => GameModeRotation.FindGameMode(gm) ?? "") ?? Array.Empty<string>();

            var sortedMaps = Configuration.Maps.Zip(Configuration.MatchesSinceSelection)
                .OrderByDescending(map => map.Second).ToList();
            var mapsWithCurrentModeSupport = sortedMaps
                .FindAll(map => FindMap(map.First).SupportedGamemodes.ConvertAll(gm => gm.GameMode).ToArray().Intersect(currentGamemodes).Any());
            var mapsThisRound = mapsWithCurrentModeSupport
                .GetRange(0, Math.Min(Configuration.MapCountInRotation, mapsWithCurrentModeSupport.Count)).ConvertAll(m => m.First).ToArray();

            this.Server.MapRotation.SetRotation(mapsThisRound);
            for (int i = 0; i < Configuration.MatchesSinceSelection.Length; i++)
            {
                Configuration.MatchesSinceSelection[i]++;
            }
            Configuration.Save();
        }
        return Task.CompletedTask;
    }

    public override void OnModulesLoaded()
    {
        this.CommandHandler.Register(this);
    }

    [CommandCallback("Maps", Description = "Shows the current map rotation", ConsoleCommand = true)]
    public void Maps(RunnerPlayer commandSource)
    {
        string maps = "";
        foreach (var map in Configuration.Maps)
        {
            maps += map + ", ";
        }
        Server.MessageToPlayer(commandSource, $"The current map rotation is: {maps}");
    }

    [CommandCallback("AddMap", Description = "Adds a map in the current rotation", ConsoleCommand = true, Permissions = new[] { "command.addmap" })]
    public void AddMap(RunnerPlayer commandSource, string map)
    {
        var matchingName = FindMapName(commandSource, map);
        if (matchingName == null) return;

        var mapIndex = Array.IndexOf(Configuration.Maps, matchingName);
        if (mapIndex != -1)
        {
            Server.SayToChat($"{matchingName} is already in rotation", commandSource);
            return;
        }
        var currentMap = BluscreamLib.Maps.ToList().Find(map => map.Name == matchingName);
        var supportedGamemodes = (currentMap != null) ? currentMap.SupportedGamemodes.ToList().
            FindAll(gm => gm.SupportedMapSizes.Contains(Server.MapSize)).ConvertAll(sgm => sgm.GameMode).ToArray() : new[] { "" };
        var gamemodesIntersection = GameModeRotation.ActiveGamemodes.Intersect(supportedGamemodes).ToArray();
        if (gamemodesIntersection.Length == 0)
        {
            Server.SayToChat($"<color=yellow>Warning {matchingName} does not support any gamemode in current rotation", commandSource);
        }

        Configuration.Maps = Configuration.Maps.Append(matchingName).ToArray();
        Configuration.Save();

        Server.SayToChat($"Successfuly added {matchingName} to rotation", commandSource);
    }

    [CommandCallback("RemoveMap", Description = "Removes a map from the current rotation", ConsoleCommand = true, Permissions = new[] { "command.removemap" })]
    public void RemoveMap(RunnerPlayer commandSource, string map)
    {
        var matchingName = FindMapName(commandSource, map);
        if (matchingName == null) return;

        var mapIndex = Array.IndexOf(Configuration.Maps, matchingName);
        if (mapIndex == -1)
        {
            Server.SayToChat($"{matchingName} is already off rotation or doesn't exist", commandSource);
            return;
        }
        Configuration.Maps = Configuration.Maps.Except(new string[] { matchingName }).ToArray();
        Configuration.Save();

        Server.SayToChat($"Successfuly removed {matchingName} from rotation", commandSource);
    }

    [CommandCallback("AddGMMaps", Description = "Adds every map that supports the selected gamemode at the current map size to the rotation", ConsoleCommand = true, Permissions = new[] { "command.addgmmaps" })]
    public void AddGMMaps(RunnerPlayer commandSource, string gamemode)
    {
        var gamemodeName = GameModeRotation.FindGameMode(gamemode);
        if (gamemodeName == null)
        {
            Server.SayToChat($"Gamemode {gamemodeName} not found", commandSource);
            return;
        }
        if (!GameModeRotation.ActiveGamemodes.ConvertAll(name => name.ToLower()).Contains(gamemodeName.ToLower()))
        {
            Server.SayToChat($"{gamemodeName} is not currently in rotation, try activating it before using this command", commandSource);
            return;
        }
        var mapsToAdd = BluscreamLib.Maps.ToList().FindAll(map =>
        {
            var index = map.SupportedGamemodes.ToList().FindIndex(gm => gm.GameMode.ToLower() == gamemodeName.ToLower());
            if (index == -1) return false;
            return map.SupportedGamemodes[index].SupportedMapSizes.Contains(Server.MapSize);
        }).ConvertAll(map => map.Name);

        if (mapsToAdd.Count == 0)
        {
            Server.SayToChat($"There are no maps that support {gamemodeName} at current server size({Server.MapSize})", commandSource);
        }

        string outputString = "";
        foreach (var map in mapsToAdd)
        {
            outputString += map + ", ";
        }

        Configuration.Maps = Configuration.Maps.Union(mapsToAdd).ToArray();
        Configuration.Save();

        Server.SayToChat($"Successfuly added {outputString} to rotation", commandSource);
    }

    [CommandCallback("MapCleanup", Description = "Removes a maps that don't support current gamemodes at current map size", ConsoleCommand = true, Permissions = new[] { "command.mapcleanup" })]
    public void MapCleanup(RunnerPlayer commandSource)
    {
        var currentRotation = GameModeRotation.ActiveGamemodes.ConvertAll(name => name.ToLower());
        var currentMapNames = Configuration.Maps.ToList();
        var currentMaps = BluscreamLib.Maps.ToList().FindAll(map => currentMapNames.Contains(map.Name));
        var unsuportedMaps = currentMaps.FindAll(map =>
        {
            var gamemodes = map.SupportedGamemodes.ToList().FindAll(gm => currentRotation.Contains(gm.GameMode.ToLower()));
            if (gamemodes.Count == 0) return true;
            return gamemodes.FindIndex(gm => gm.SupportedMapSizes.Contains(Server.MapSize)) == -1;
        }).ConvertAll(m => m.Name);



        if (unsuportedMaps.Count == 0)
        {
            Server.SayToChat($"All current maps support the current gamemodes", commandSource);
        }

        string outputString = "";
        foreach (var map in unsuportedMaps)
        {

            outputString += map + ", ";
        }

        Configuration.Maps = Configuration.Maps.Except(unsuportedMaps).ToArray();
        Configuration.Save();

        Server.SayToChat($"Successfuly removed {outputString} from rotation", commandSource);
    }

    private string? FindMapName(RunnerPlayer commandSource, string mapName)
    {
        return mapName.ParseMap()?.First()?.Name;
    }
    private static MapInfo? FindMap(string mapName) {
        return mapName.ParseMap()?.FirstOrDefault();
    }
    private void ReinicializeCounters()
    {
        this.Logger.Info($"{Server.ServerName} MapRotation: reinicializing maps counter");
        Configuration.MatchesSinceSelection = new int[Configuration.Maps.Length];
        Random r = new();
        for (int i = 0; i < Configuration.Maps.Length; i++)
        {
            Configuration.MatchesSinceSelection[i] = r.Next(5);
        }
        Configuration.Save();
    }

    /*//use for debugging
    [CommandCallback("M", Description = "Shows how many matches since the last time a map was played", ConsoleCommand = true, Permissions = new[] { "command.m" })]
    public void M(RunnerPlayer commandSource)
    {
        string maps = "";
        int i = 0;
        foreach (var map in Configuration.Maps)
        {
            maps += map + " " + matchesSinceSelection[i] + ", ";
            i++;
        }
        Server.SayToChat($"maps played and times since last played: {maps}", commandSource);
    }
    [CommandCallback("CM", Description = "Shows the Current Map name returned by Server.map", ConsoleCommand = true, Permissions = new[] { "command.cm" })]
    public void CM(RunnerPlayer commandSource)
    {
        Server.MessageToPlayer(commandSource, $"Current map {Server.Map}");
    }*/
}

public class MapRotationConfiguration : ModuleConfiguration
{
    public int MapCountInRotation { get; set; } = 8;
    public int[] MatchesSinceSelection { get; set; } = new int[1];
    public string[] Maps { get; set; } = new[]
    {
        "Azagor",
        "Basra",
        "Construction",
        "District",
        "Dustydew",
        "Eduardovo",
        "Frugis",
        "Isle",
        "Kodiak",
        "Lonovo",
        "Old_Eduardovo",
        "MultuIslands",
        "Namak",
        "Old_District",
        "Old_MultuIslands",
        "Old_Namak",
        "OilDunes",
        "Old_OilDunes",
        "WineParadise",
        "River",
        "Salhan",
        "SandySunset",
        "TensaTown",
        "Valley",
        "Wakistan",
        "ZalfiBay"
    };
}