﻿using System;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Logging;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.SeFunctions;
using GatherBuddy.Time;
using GatherBuddy.Utility;
using CommandManager = GatherBuddy.SeFunctions.CommandManager;
using GatheringType = GatherBuddy.Enums.GatheringType;

namespace GatherBuddy.Plugin;

public class Executor
{
    private enum IdentifyType
    {
        None,
        Item,
        Fish,
    }

    private readonly CommandManager _commandManager = new(Dalamud.GameGui, Dalamud.SigScanner);
    private readonly MacroManager   _macroManager   = new();
    public readonly  Identificator  Identificator   = new();

    private IdentifyType _identifyType = IdentifyType.None;
    private string       _name         = string.Empty;

    private IGatherable? _item = null;

    private GatheringType? _gatheringType = null;
    private ILocation?     _location      = null;
    private TimeInterval   _uptime        = TimeInterval.Always;

    private void FindGatherableLogged(string itemName)
    {
        _item = Identificator.IdentifyGatherable(itemName);
        if (_item == null)
        {
            Communicator.ItemNotFound(itemName);
            return;
        }

        if (GatherBuddy.Config.IdentifiedItemFormat.Length > 0)
            Communicator.Print(Communicator.FormatIdentifiedItemMessage(GatherBuddy.Config.IdentifiedItemFormat, itemName, _item));
        PluginLog.Verbose(Configuration.DefaultIdentifiedItemFormat, _item.ItemId, _item.Name[GatherBuddy.Language], itemName);
    }

    private void FindFishLogged(string fishName)
    {
        _item = Identificator.IdentifyFish(fishName);
        if (_item == null)
        {
            Communicator.ItemNotFound(fishName);
            return;
        }

        if (GatherBuddy.Config.IdentifiedFishFormat.Length > 0)
            Communicator.Print(Communicator.FormatIdentifiedItemMessage(GatherBuddy.Config.IdentifiedFishFormat, fishName, _item));
        PluginLog.Verbose(Configuration.DefaultIdentifiedFishFormat, _item.ItemId, _item.Name[GatherBuddy.Language], fishName);
    }

    private void DoIdentify()
    {
        if (_name.Length == 0)
            return;

        switch (_identifyType)
        {
            case IdentifyType.None: return;
            case IdentifyType.Item:
                FindGatherableLogged(_name);
                return;
            case IdentifyType.Fish:
                FindFishLogged(_name);
                return;
            default: throw new ArgumentOutOfRangeException();
        }
    }

    private void FindClosestLocation()
    {
        if (_item == null)
            return;

        _location = null;
        if (_gatheringType == null || _item is Fish)
            (_location, _uptime) = GatherBuddy.UptimeManager.BestLocation(_item);
        else
            (_location, _uptime) = GatherBuddy.UptimeManager.NextUptime((Gatherable)_item, _gatheringType.Value, GatherBuddy.Time.ServerTime);

        if (_location == null)
            Communicator.LocationNotFound(_item, _gatheringType);
    }

    private void DoTeleport()
    {
        if (!GatherBuddy.Config.UseTeleport || _location?.ClosestAetheryte == null)
            return;

        if (GatherBuddy.Config.SkipTeleportIfClose
         && Dalamud.ClientState.TerritoryType == _location.Territory.Id
         && Dalamud.ClientState.LocalPlayer != null)
        {
            // Check distance of player to node against distance of aetheryte to node.
            var playerPos = Dalamud.ClientState.LocalPlayer.Position;
            var aetheryte = _location.ClosestAetheryte;
            var posX      = Maps.NodeToMap(playerPos.X, _location.Territory.SizeFactor);
            var posY      = Maps.NodeToMap(playerPos.Z, _location.Territory.SizeFactor);
            var distAetheryte = aetheryte != null
                ? System.Math.Sqrt(aetheryte.WorldDistance(_location.Territory.Id, _location.IntegralXCoord, _location.IntegralYCoord))
                : double.PositiveInfinity;
            var distPlayer = System.Math.Sqrt(Utility.Math.SquaredDistance(posX, posY, _location.IntegralXCoord, _location.IntegralYCoord));
            // Allow for some leeway due to teleport cost and time.
            if (distPlayer < distAetheryte * 1.5)
                return;
        }

        TeleportToAetheryte(_location.ClosestAetheryte);
    }

    private void DoGearChange()
    {
        if (!GatherBuddy.Config.UseGearChange || _location == null)
            return;

        var set = _location.GatheringType.ToGroup() switch
        {
            GatheringType.Fisher   => GatherBuddy.Config.FisherSetName,
            GatheringType.Botanist => GatherBuddy.Config.BotanistSetName,
            GatheringType.Miner    => GatherBuddy.Config.MinerSetName,
            _                      => null,
        };
        if (set == null)
        {
            Communicator.PrintError("No job type associated with location ", _location.Name, GatherBuddy.Config.SeColorArguments, ".");
            return;
        }

        if (set.Length == 0)
        {
            Communicator.PrintError("No gear set for ", _location.GatheringType.ToString(), GatherBuddy.Config.SeColorArguments, " configured.");
            return;
        }


        _commandManager.Execute($"/gearset change \"{set}\"");
    }


    private void DoMapFlag()
    {
        if (!GatherBuddy.Config.WriteCoordinates && !GatherBuddy.Config.UseCoordinates || _location == null)
            return;

        if (_location.IntegralXCoord == 100 || _location.IntegralYCoord == 100)
            return;

        var link = Communicator
            .AddFullMapLink(new SeStringBuilder(), _location.Name, _location.Territory, _location.IntegralXCoord / 100f,
                _location.IntegralYCoord / 100f,   true).BuiltString;
        if (GatherBuddy.Config.WriteCoordinates)
            Communicator.Print(link);
    }

    private void DoAdditionalInfo()
    {
        if (!GatherBuddy.Config.PrintUptime || _uptime.Equals(TimeInterval.Always))
            return;

        if (_uptime.Start > GatherBuddy.Time.ServerTime)
            Communicator.Print("Next up in ", TimeInterval.DurationString(_uptime.Start, GatherBuddy.Time.ServerTime, false), GatherBuddy.Config.SeColorArguments, ".");
        else
            Communicator.Print("Currently up for the next ", TimeInterval.DurationString(_uptime.End, GatherBuddy.Time.ServerTime, false), GatherBuddy.Config.SeColorArguments, ".");
    }

    public bool DoCommand(string argument)
    {
        switch (argument)
        {
            case GatherBuddy.IdentifyCommand:
                DoIdentify();
                FindClosestLocation();
                return true;
            case GatherBuddy.MapMarkerCommand:
                DoMapFlag();
                return true;
            case GatherBuddy.GearChangeCommand:
                DoGearChange();
                return true;
            case GatherBuddy.TeleportCommand:
                DoTeleport();
                return true;
            case GatherBuddy.AdditionalInfoCommand:
                DoAdditionalInfo();
                return true;
            default: return false;
        }
    }

    public void GatherLocation(ILocation location)
    {
        _identifyType  = IdentifyType.None;
        _name          = string.Empty;
        _item          = null;
        _gatheringType = location.GatheringType.ToGroup();
        _location      = location;
        if (location is GatheringNode n)
            _uptime = n.Times.NextUptime(GatherBuddy.Time.ServerTime);
        else
            _uptime = TimeInterval.Always;

        _macroManager.Execute();
    }

    public void GatherItem(IGatherable? item, GatheringType? type = null)
    {
        if (item == null)
            return;

        _identifyType  = IdentifyType.None;
        _name          = string.Empty;
        _item          = item;
        _location      = null;
        _gatheringType = type?.ToGroup();
        _uptime        = TimeInterval.Always;

        _macroManager.Execute();
    }

    public void GatherFishByName(string fishName)
    {
        if (fishName.Length == 0)
            return;

        _identifyType  = IdentifyType.Fish;
        _name          = fishName;
        _item          = null;
        _location      = null;
        _gatheringType = null;
        _uptime        = TimeInterval.Always;

        _macroManager.Execute();
    }

    public void GatherItemByName(string itemName, GatheringType? type = null)
    {
        if (itemName.Length == 0)
            return;

        _identifyType  = IdentifyType.Item;
        _name          = itemName;
        _item          = null;
        _location      = null;
        _gatheringType = type;
        _uptime        = TimeInterval.Always;

        _macroManager.Execute();
    }

    public static void TeleportToAetheryte(Aetheryte aetheryte)
    {
        if (aetheryte.Id == 0)
            return;

        Teleporter.Teleport(aetheryte.Id);
    }

    public static void TeleportToTerritory(Territory territory)
    {
        if (territory.Aetherytes.Count == 0)
        {
            Communicator.PrintError(string.Empty, territory.Name, GatherBuddy.Config.SeColorArguments, " has no valid aetheryte.");
            return;
        }

        var aetheryte = territory.Aetherytes.FirstOrDefault(a => Teleporter.IsAttuned(a.Id));
        if (aetheryte == null)
        {
            Communicator.PrintError("Not attuned to any aetheryte in ", territory.Name, GatherBuddy.Config.SeColorArguments, ".");
            return;
        }

        Teleporter.TeleportUnchecked(aetheryte.Id);
    }
}