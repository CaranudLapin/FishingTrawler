﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FishingTrawler.API;
using FishingTrawler.GameLocations;
using FishingTrawler.Messages;
using FishingTrawler.Objects;
using FishingTrawler.Objects.Rewards;
using FishingTrawler.Objects.Tools;
using FishingTrawler.Patches.Locations;
using FishingTrawler.UI;
using Harmony;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.Tools;

namespace FishingTrawler
{
    public class ModEntry : Mod
    {
        internal static IMonitor monitor;
        internal static IModHelper modHelper;
        internal static IManifest manifest;
        internal static Multiplayer multiplayer;
        internal static int fishingTripTimer;
        internal static string trawlerThemeSong;
        internal static bool themeSongUpdated;
        internal static bool claimedBoat;
        internal static int numberOfDeckhands;

        private static FlagType _hoistedFlag;
        private static TrawlerRewards _trawlerRewards;

        // Trawler beach map related
        internal static Murphy murphyNPC;
        internal static Trawler trawlerObject;
        internal static Chest rewardChest;

        // Trawler map / texture related
        private TrawlerHull _trawlerHull;
        private TrawlerSurface _trawlerSurface;
        private TrawlerCabin _trawlerCabin;
        private string _trawlerItemsPath = Path.Combine("assets", "TrawlerItems");

        // Location names
        private const string TRAWLER_SURFACE_LOCATION_NAME = "Custom_FishingTrawler";
        private const string TRAWLER_HULL_LOCATION_NAME = "Custom_TrawlerHull";
        private const string TRAWLER_CABIN_LOCATION_NAME = "Custom_TrawlerCabin";

        // Day to appear settings
        internal const int BOAT_DEPART_EVENT_ID = 411203900;
        private const string DAY_TO_APPEAR_TOWN = "Wed";
        private const string DAY_TO_APPEAR_ISLAND = "Sun";

        // Mod data related
        private const string REWARD_CHEST_DATA_KEY = "PeacefulEnd.FishingTrawler_RewardChest";
        internal const string MURPHY_WAS_GREETED_TODAY_KEY = "PeacefulEnd.FishingTrawler_MurphyGreeted";
        internal const string MURPHY_SAILED_TODAY_KEY = "PeacefulEnd.FishingTrawler_MurphySailedToday";
        internal const string MURPHY_WAS_TRIP_SUCCESSFUL_KEY = "PeacefulEnd.FishingTrawler_MurphyTripSuccessful";
        internal const string MURPHY_FINISHED_TALKING_KEY = "PeacefulEnd.FishingTrawler_MurphyFinishedTalking";
        internal const string MURPHY_HAS_SEEN_FLAG_KEY = "PeacefulEnd.FishingTrawler_MurphyHasSeenFlag";

        internal const string BAILING_BUCKET_KEY = "PeacefulEnd.FishingTrawler_BailingBucket";
        internal const string ANCIENT_FLAG_KEY = "PeacefulEnd.FishingTrawler_AncientFlag";

        internal const string HOISTED_FLAG_KEY = "PeacefulEnd.FishingTrawler_HoistedFlag";

        // Notificiation messages
        private readonly KeyValuePair<string, int> MESSAGE_EVERYTHING_FAILING = new KeyValuePair<string, int>("This ship is falling apart!", 10);
        private readonly KeyValuePair<string, int> MESSAGE_LOSING_FISH = new KeyValuePair<string, int>("We're losing fish!", 9);
        private readonly KeyValuePair<string, int> MESSAGE_MAX_LEAKS = new KeyValuePair<string, int>("We're taking on water!", 8);
        private readonly KeyValuePair<string, int> MESSAGE_MULTI_PROBLEMS = new KeyValuePair<string, int>("We've got lots of problems!", 7);
        private readonly KeyValuePair<string, int> MESSAGE_ENGINE_PROBLEM = new KeyValuePair<string, int>("The engine is failing!", 7);
        private readonly KeyValuePair<string, int> MESSAGE_NET_PROBLEM = new KeyValuePair<string, int>("The nets are torn!", 6);
        private readonly KeyValuePair<string, int> MESSAGE_LEAK_PROBLEM = new KeyValuePair<string, int>("We've got a leak!", 5);

        // Notification related
        private uint _eventSecondInterval;
        private bool _isTripEnding;
        private bool _isNotificationFading;
        private float _notificationAlpha;
        private string _activeNotification;

        // API related
        //IContentPatcherAPI contentPatcherApi;

        public override void Entry(IModHelper helper)
        {
            // Set up the monitor and helper
            monitor = Monitor;
            modHelper = helper;
            manifest = ModManifest;
            multiplayer = helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();

            // Load in our assets
            ModResources.SetUpAssets(helper);

            // Initialize the timer for fishing trip
            fishingTripTimer = 0;

            // Set up our notification on the trawler
            _eventSecondInterval = 600;
            _isTripEnding = false;
            _activeNotification = String.Empty;
            _notificationAlpha = 1f;
            _isNotificationFading = false;

            // Load our Harmony patches
            try
            {
                var harmony = HarmonyInstance.Create(this.ModManifest.UniqueID);

                // Apply our patches
                new BeachPatch(monitor).Apply(harmony);
                new GameLocationPatch(monitor).Apply(harmony);
            }
            catch (Exception e)
            {
                Monitor.Log($"Issue with Harmony patching: {e}", LogLevel.Error);
                return;
            }

            // Hook into GameLoops related events
            helper.Events.GameLoop.UpdateTicking += this.OnUpdateTicking; ;
            helper.Events.GameLoop.OneSecondUpdateTicking += this.OnOneSecondUpdateTicking;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.DayEnding += this.OnDayEnding;

            // Hook into Display related events
            helper.Events.Display.RenderingHud += this.OnRenderingHud;
            helper.Events.Display.RenderedHud += this.OnRenderedHud;

            // Hook into Player related events
            helper.Events.Player.Warped += this.OnWarped;

            // Hook into MouseClicked
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;

            // Hook into Multiplayer related
            helper.Events.Multiplayer.PeerConnected += this.OnPeerConnected;
            helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
        }

        private void OnModMessageReceived(object sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID == this.ModManifest.UniqueID && e.Type == nameof(DepartureMessage))
            {
                trawlerObject.TriggerDepartureEvent();
            }
        }

        private void OnPeerConnected(object sender, PeerConnectedEventArgs e)
        {
            if (!Context.IsMainPlayer)
            {
                // Set Farmer moddata used for this mod
                EstablishPlayerData();
            }
        }

        private void OnRenderingHud(object sender, RenderingHudEventArgs e)
        {
            if (!IsPlayerOnTrawler())
            {
                return;
            }

            if (!String.IsNullOrEmpty(_activeNotification))
            {
                TrawlerUI.DrawNotification(e.SpriteBatch, Game1.player.currentLocation, _activeNotification, _notificationAlpha);
            }
        }

        private void OnRenderedHud(object sender, RenderedHudEventArgs e)
        {
            if (!IsPlayerOnTrawler())
            {
                return;
            }

            TrawlerUI.DrawUI(e.SpriteBatch, fishingTripTimer, _trawlerSurface.fishCaughtQuantity, _trawlerHull.waterLevel, _trawlerHull.HasLeak(), _trawlerSurface.GetRippedNetsCount(), _trawlerCabin.GetLeakingPipesCount());
        }

        private void OnWarped(object sender, WarpedEventArgs e)
        {
            // Check if player just left the trawler
            if (!IsPlayerOnTrawler() && IsValidTrawlerLocation(e.OldLocation))
            {
                if (claimedBoat)
                {
                    foreach (Farmer farmer in GetFarmersOnTrawler())
                    {
                        farmer.warpFarmer(new Warp(0, 0, "Beach", 86, 38, false));
                    }

                    // Give the player(s) their rewards, if they left the trawler as expected (warping out early does not give any rewards)
                    if (_isTripEnding)
                    {
                        _trawlerRewards.CalculateAndPopulateReward(numberOfDeckhands, _trawlerSurface.fishCaughtQuantity);
                    }

                    // Reset the trawler
                    _trawlerHull.Reset();
                    _trawlerSurface.Reset();
                    _trawlerCabin.Reset();

                    // Set the theme to null
                    SetTrawlerTheme(null);
                }

                // Take away any bailing buckets
                foreach (BailingBucket bucket in Game1.player.Items.Where(i => i != null && i is BailingBucket))
                {
                    Game1.player.removeItemFromInventory(bucket);
                }

                // Get XP reward
                _trawlerRewards.GetFishingXP(e.Player);

                // Finish trip ending logic
                _isTripEnding = false;

                numberOfDeckhands = 0;
                claimedBoat = false;

                return;
            }

            // Check if player just entered the trawler
            if (IsPlayerOnTrawler() && !IsValidTrawlerLocation(e.OldLocation))
            {
                // Set the default track
                Game1.changeMusicTrack("fieldofficeTentMusic");

                // Give them a bailing bucket
                if (!Game1.player.items.Any(i => i is BailingBucket))
                {
                    Game1.player.addItemToInventory(new BailingBucket());
                    Game1.addHUDMessage(new HUDMessage("A bailing bucket has been added to your inventory.", null));
                }

                if (claimedBoat)
                {
                    // Clear any previous reward data, set the head deckhand (which determines fishing level for reward calc)
                    _trawlerRewards.Reset(Game1.player);

                    // Start the timer (2.5 minute default)
                    fishingTripTimer = 60000; //150000
                    _trawlerSurface.fishCaughtQuantity = 100;

                    // Set flag data
                    _trawlerSurface.SetFlagTexture(_hoistedFlag);

                    // Apply flag benefits
                    switch (_hoistedFlag)
                    {
                        case FlagType.Parley:
                            // Disable all leaks, but reduce fish catch chance by 25% during reward calculations (e.g. more chance of junk / lower quality fish)
                            _trawlerHull.areLeaksEnabled = false;
                            _trawlerRewards.fishCatchChanceOffset = 0.25f;
                            break;
                        case FlagType.JollyRoger:
                            // Quadruples net output 
                            _trawlerSurface.fishCaughtMultiplier = 4;
                            _trawlerHull.hasWeakHull = true;
                            break;
                        case FlagType.GamblersCrest:
                            // 50% of doubling chest, 25% of getting nothing
                            _trawlerRewards.isGambling = true;
                            break;
                        case FlagType.MermaidsBlessing:
                            // 10% of fish getting consumed, but gives random fishing chest reward
                            _trawlerRewards.hasMermaidsBlessing = true;
                            break;
                        case FlagType.PatronSaint:
                            // 25% of fish getting consumed, but gives full XP
                            _trawlerRewards.hasPatronSaint = true;
                            break;
                        case FlagType.SharksFin:
                            // Adds one extra minute to timer, allowing for more fish haul
                            fishingTripTimer += 60000;
                            break;
                        case FlagType.Worldly:
                            // Allows catching of non-ocean fish
                            _trawlerRewards.hasWorldly = true;
                            break;
                        default:
                            // Do nothing
                            break;
                    }
                }

                return;
            }
        }

        private void OnUpdateTicking(object sender, UpdateTickingEventArgs e)
        {
            if (!Context.IsWorldReady || !IsPlayerOnTrawler() || _isTripEnding)
            {
                return;
            }

            // Every quarter of a second play leaking sound, if there is a leak
            if (e.IsMultipleOf(15))
            {
                if (Game1.player.currentLocation is TrawlerHull && _trawlerHull.HasLeak())
                {
                    Game1.playSoundPitched("wateringCan", Game1.random.Next(1, 5) * 100);
                }
            }

            if (claimedBoat)
            {
                if (Game1.activeClickableMenu != null && !Context.IsMultiplayer)
                {
                    // Allow pausing in singleplayer via menu
                    return;
                }

                if (_isNotificationFading)
                {
                    _notificationAlpha -= 0.1f;
                }

                if (_notificationAlpha < 0f)
                {
                    _activeNotification = String.Empty;
                    _isNotificationFading = false;
                    _notificationAlpha = 1f;
                }


                if (e.IsMultipleOf(150))
                {
                    if (!String.IsNullOrEmpty(_activeNotification))
                    {
                        _isNotificationFading = true;
                    }

                    // Update water level (from leaks) every second
                    _trawlerHull.RecaculateWaterLevel();

                    if (_trawlerHull.waterLevel == 100)
                    {
                        // Reduce fishCaughtQuantity due to failed trip
                        _trawlerSurface.fishCaughtQuantity /= 4;
                        DelayedAction.warpAfterDelay("Beach", new Point(86, 38), 2500);
                    }
                }
            }

            if (_trawlerHull.waterLevel == 100)
            {
                // Set the status as failed
                Game1.player.modData[MURPHY_WAS_TRIP_SUCCESSFUL_KEY] = "false";
                Game1.player.modData[MURPHY_SAILED_TODAY_KEY] = "true";

                // End trip due to flooding
                Game1.player.currentLocation.playSound("fishEscape");
                Game1.player.CanMove = false;
                Game1.addHUDMessage(new HUDMessage("The ship has taken on too much water! Murphy quickly returns to port before it can sink.", null));

                _isTripEnding = true;
            }
        }

        private void OnOneSecondUpdateTicking(object sender, OneSecondUpdateTickingEventArgs e)
        {
            if (!Context.IsWorldReady || !IsPlayerOnTrawler() || _isTripEnding)
            {
                return;
            }

            if (claimedBoat)
            {
                if (Game1.activeClickableMenu != null && !Context.IsMultiplayer)
                {
                    // Allow pausing in singleplayer via menu
                    return;
                }

                // Iterate the fishing trip timer
                if (fishingTripTimer > 0f)
                {
                    fishingTripTimer -= 1000;
                }

                // Update the track if needed
                if (themeSongUpdated)
                {
                    themeSongUpdated = false;

                    _trawlerCabin.miniJukeboxTrack.Value = String.IsNullOrEmpty(trawlerThemeSong) ? null : trawlerThemeSong;
                    _trawlerHull.miniJukeboxTrack.Value = String.IsNullOrEmpty(trawlerThemeSong) ? null : trawlerThemeSong;
                    _trawlerSurface.miniJukeboxTrack.Value = String.IsNullOrEmpty(trawlerThemeSong) ? null : trawlerThemeSong;
                }

                // Every 5 seconds recalculate the amount of fish caught / lost
                if (e.IsMultipleOf(300))
                {
                    _trawlerSurface.UpdateFishCaught(_trawlerCabin.AreAllPipesLeaking());
                }

                // Every random interval check for new event (leak, net tearing, etc.) on Trawler
                if (e.IsMultipleOf(_eventSecondInterval))
                {
                    string message = String.Empty;

                    // Check if the player gets lucky and skips getting an event, otherwise create the event(s)
                    if (Game1.random.NextDouble() < 0.05)
                    {
                        message = "The sea favors us today!";
                    }
                    else
                    {
                        // TODO: Sync area changes via broadcasts
                        message = CreateTrawlerEventsAndGetMessage();
                    }

                    // Check for empty string 
                    if (String.IsNullOrEmpty(message))
                    {
                        message = "Ah the smell of the sea...";
                    }

                    if (_activeNotification != message)
                    {
                        _activeNotification = message;
                    }

                    _eventSecondInterval = (uint)Game1.random.Next(1, 5) * 100;
                }
            }

            if (fishingTripTimer <= 0f)
            {
                // Set the status as successful
                Game1.player.modData[MURPHY_WAS_TRIP_SUCCESSFUL_KEY] = "true";
                Game1.player.modData[MURPHY_SAILED_TODAY_KEY] = "true";

                // End trip due to timer finishing
                Game1.player.currentLocation.playSound("trainWhistle");
                Game1.player.CanMove = false;

                _isTripEnding = true;

                if (claimedBoat)
                {
                    Game1.addHUDMessage(new HUDMessage("The trip was a success! Murphy starts heading back to port.", null));
                    DelayedAction.warpAfterDelay("Beach", new Point(86, 38), 2000);
                }
            }
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if ((!e.IsDown(SButton.MouseRight) && !e.IsDown(Buttons.A.ToSButton())) || !Context.IsWorldReady || Game1.activeClickableMenu != null)
            {
                return;
            }

            if (e.IsDown(Buttons.A.ToSButton()))
            {
                if (Game1.player.currentLocation.NameOrUniqueName == TRAWLER_HULL_LOCATION_NAME)
                {
                    for (int y = 0; y < 4; y++)
                    {
                        _trawlerHull.AttemptPlugLeak((int)Game1.player.getTileX(), (int)Game1.player.getTileY() - y, Game1.player);
                    }
                }
                else if (Game1.player.currentLocation.NameOrUniqueName == TRAWLER_SURFACE_LOCATION_NAME)
                {
                    for (int y = 0; y < 3; y++)
                    {
                        _trawlerSurface.AttemptFixNet((int)Game1.player.getTileX(), (int)Game1.player.getTileY() - y, Game1.player);
                    }
                }
                else if (Game1.player.currentLocation.NameOrUniqueName == TRAWLER_CABIN_LOCATION_NAME)
                {
                    for (int y = 0; y < 3; y++)
                    {
                        _trawlerCabin.AttemptPlugLeak((int)Game1.player.getTileX(), (int)Game1.player.getTileY() - y, Game1.player);
                    }
                }
            }
            else
            {
                if (Game1.player.currentLocation.NameOrUniqueName == TRAWLER_HULL_LOCATION_NAME)
                {
                    _trawlerHull.AttemptPlugLeak((int)e.Cursor.Tile.X, (int)e.Cursor.Tile.Y, Game1.player);
                }
                else if (Game1.player.currentLocation.NameOrUniqueName == TRAWLER_SURFACE_LOCATION_NAME)
                {
                    // Attempt two checks, in case the user clicks above the rope
                    _trawlerSurface.AttemptFixNet((int)e.Cursor.Tile.X, (int)e.Cursor.Tile.Y, Game1.player);
                    _trawlerSurface.AttemptFixNet((int)e.Cursor.Tile.X, (int)e.Cursor.Tile.Y + 1, Game1.player);
                }
                else if (Game1.player.currentLocation.NameOrUniqueName == TRAWLER_CABIN_LOCATION_NAME)
                {
                    _trawlerCabin.AttemptPlugLeak((int)e.Cursor.Tile.X, (int)e.Cursor.Tile.Y, Game1.player);
                }
            }
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            // May hook into Content Patcher's API for tokens
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            Beach beach = Game1.getLocationFromName("Beach") as Beach;

            // Set Farmer moddata used for this mod
            EstablishPlayerData();

            if (Context.IsMainPlayer)
            {
                // Must be a Wednesday, the player's fishing level >= 3 and the bridge must be fixed on the beach
                if (!Game1.MasterPlayer.mailReceived.Contains("PeacefulEnd.FishingTrawler_WillyIntroducesMurphy") && Game1.MasterPlayer.FishingLevel >= 3 && beach.bridgeFixed && Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth) == DAY_TO_APPEAR_TOWN)
                {
                    Helper.Content.AssetEditors.Add(new IntroMail());
                    Game1.MasterPlayer.mailbox.Add("PeacefulEnd.FishingTrawler_WillyIntroducesMurphy");
                }

                // Set the reward chest
                Vector2 rewardChestPosition = new Vector2(-100, -100);
                Farm farm = Game1.getLocationFromName("Farm") as Farm;
                rewardChest = farm.objects.Values.FirstOrDefault(o => o.modData.ContainsKey(REWARD_CHEST_DATA_KEY)) as Chest;
                if (rewardChest is null)
                {
                    Monitor.Log($"Creating reward chest {rewardChestPosition}", LogLevel.Trace);
                    rewardChest = new Chest(true, rewardChestPosition) { Name = "Trawler Rewards" };
                    rewardChest.modData.Add(REWARD_CHEST_DATA_KEY, "true");

                    farm.setObject(rewardChestPosition, rewardChest);
                }

                // Create the TrawlerReward class
                _trawlerRewards = new TrawlerRewards(rewardChest);

                // Create the trawler object for the beach
                trawlerObject = new Trawler(beach);
            }

            // Add the surface location
            TrawlerSurface surfaceLocation = new TrawlerSurface(Path.Combine(ModResources.assetFolderPath, "Maps", "FishingTrawler.tmx"), TRAWLER_SURFACE_LOCATION_NAME) { IsOutdoors = true, IsFarm = false };
            Game1.locations.Add(surfaceLocation);

            // Add the hull location
            TrawlerHull hullLocation = new TrawlerHull(Path.Combine(ModResources.assetFolderPath, "Maps", "TrawlerHull.tmx"), TRAWLER_HULL_LOCATION_NAME) { IsOutdoors = false, IsFarm = false };
            Game1.locations.Add(hullLocation);

            // Add the cabin location
            TrawlerCabin cabinLocation = new TrawlerCabin(Path.Combine(ModResources.assetFolderPath, "Maps", "TrawlerCabin.tmx"), TRAWLER_CABIN_LOCATION_NAME) { IsOutdoors = false, IsFarm = false };
            Game1.locations.Add(cabinLocation);


            // Verify our locations were added and establish our location variables
            _trawlerHull = Game1.getLocationFromName(TRAWLER_HULL_LOCATION_NAME) as TrawlerHull;
            _trawlerSurface = Game1.getLocationFromName(TRAWLER_SURFACE_LOCATION_NAME) as TrawlerSurface;
            _trawlerCabin = Game1.getLocationFromName(TRAWLER_CABIN_LOCATION_NAME) as TrawlerCabin;

            // Reset ownership of boat, deckhands
            claimedBoat = false;
            numberOfDeckhands = 0;
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            // Save the current hoisted flag
            Game1.player.modData[HOISTED_FLAG_KEY] = _hoistedFlag.ToString();

            // Offload the custom locations
            Game1.locations.Remove(_trawlerHull);
            Game1.locations.Remove(_trawlerSurface);
            Game1.locations.Remove(_trawlerCabin);
        }

        private string CreateTrawlerEventsAndGetMessage()
        {
            int amountOfEvents = 0;
            for (int x = 0; x < 4; x++)
            {
                // Chance of skipping an event increases with each pass of this loop
                if (Game1.random.NextDouble() < 0.1 + (x * 0.1f))
                {
                    // Skip event
                    continue;
                }

                amountOfEvents++;
            }

            int executedEvents = 0;
            List<KeyValuePair<string, int>> possibleMessages = new List<KeyValuePair<string, int>>();
            for (int x = 0; x < amountOfEvents; x++)
            {
                if (!_trawlerSurface.AreAllNetsRipped() && Game1.random.NextDouble() < 0.35)
                {
                    _trawlerSurface.AttemptCreateNetRip();
                    possibleMessages.Add(_trawlerSurface.AreAllNetsRipped() && _trawlerCabin.AreAllPipesLeaking() ? MESSAGE_LOSING_FISH : MESSAGE_NET_PROBLEM);

                    executedEvents++;
                    continue;
                }

                if (!_trawlerCabin.AreAllPipesLeaking() && Game1.random.NextDouble() < 0.25)
                {
                    _trawlerCabin.AttemptCreatePipeLeak();
                    possibleMessages.Add(_trawlerSurface.AreAllNetsRipped() && _trawlerCabin.AreAllPipesLeaking() ? MESSAGE_LOSING_FISH : MESSAGE_ENGINE_PROBLEM);

                    executedEvents++;
                    continue;
                }

                // Default hull breaking event
                if (!_trawlerHull.AreAllHolesLeaking() && _trawlerHull.areLeaksEnabled)
                {
                    _trawlerHull.AttemptCreateHullLeak();
                    if (_trawlerHull.hasWeakHull)
                    {
                        _trawlerHull.ForceAllHolesToLeak();
                    }

                    possibleMessages.Add(_trawlerHull.AreAllHolesLeaking() ? MESSAGE_MAX_LEAKS : MESSAGE_LEAK_PROBLEM);

                    executedEvents++;
                    continue;
                }
            }

            // Check if all possible events are activated
            if (_trawlerSurface.AreAllNetsRipped() && _trawlerCabin.AreAllPipesLeaking() && _trawlerHull.AreAllHolesLeaking())
            {
                possibleMessages.Add(MESSAGE_EVERYTHING_FAILING);
            }

            // Add a generic message if there are lots of issues
            if (executedEvents > 1)
            {
                possibleMessages.Add(MESSAGE_MULTI_PROBLEMS);
            }

            // Select highest priority item (priority == default_priority_level * frequency)
            return amountOfEvents == 0 ? "Yoba be praised!" : possibleMessages.OrderByDescending(m => m.Value * possibleMessages.Count(p => p.Key == m.Key)).FirstOrDefault().Key;
        }

        internal static void AlertPlayersOfDeparture(List<Farmer> farmersToAlert)
        {
            modHelper.Multiplayer.SendMessage(new DepartureMessage(), nameof(DepartureMessage), new[] { manifest.UniqueID }, farmersToAlert.Select(f => f.UniqueMultiplayerID).ToArray());
        }

        internal static void SetTrawlerTheme(string songName)
        {
            trawlerThemeSong = songName;
            themeSongUpdated = true;
        }

        internal static bool IsPlayerOnTrawler()
        {
            return IsValidTrawlerLocation(Game1.player.currentLocation);
        }

        private static bool IsValidTrawlerLocation(GameLocation location)
        {
            switch (location)
            {
                case TrawlerSurface surface:
                case TrawlerHull hull:
                case TrawlerCabin cabin:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool ShouldMurphyAppear(GameLocation location)
        {
            if (Game1.MasterPlayer.mailReceived.Contains("PeacefulEnd.FishingTrawler_WillyIntroducesMurphy") && location is Beach && !Game1.isStartingToGetDarkOut() && Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth) == DAY_TO_APPEAR_TOWN)
            {
                return true;
            }

            return false;
        }

        internal static FlagType GetHoistedFlag()
        {
            return _hoistedFlag;
        }

        internal static void SetHoistedFlag(FlagType flagType)
        {
            // Updating the player's modData for which flag is hoisted
            _hoistedFlag = flagType;
        }

        private void EstablishPlayerData()
        {
            if (!Game1.player.modData.ContainsKey(HOISTED_FLAG_KEY))
            {
                Game1.player.modData.Add(HOISTED_FLAG_KEY, FlagType.Unknown.ToString());
            }
            else
            {
                SetHoistedFlag(Enum.TryParse(Game1.player.modData[HOISTED_FLAG_KEY], out FlagType flagType) ? flagType : FlagType.Unknown);
            }

            if (!Game1.player.modData.ContainsKey(MURPHY_WAS_GREETED_TODAY_KEY))
            {
                Game1.player.modData.Add(MURPHY_WAS_GREETED_TODAY_KEY, "false");
            }
            else if (Game1.player.modData[MURPHY_WAS_GREETED_TODAY_KEY].ToLower() == "true")
            {
                Game1.player.modData[MURPHY_WAS_GREETED_TODAY_KEY] = "false";
            }

            if (!Game1.player.modData.ContainsKey(MURPHY_SAILED_TODAY_KEY))
            {
                Game1.player.modData.Add(MURPHY_SAILED_TODAY_KEY, "false");
                Game1.player.modData.Add(MURPHY_WAS_TRIP_SUCCESSFUL_KEY, "false");
                Game1.player.modData.Add(MURPHY_FINISHED_TALKING_KEY, "false");
            }
            else if (Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth) == DAY_TO_APPEAR_TOWN || Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth) == DAY_TO_APPEAR_ISLAND)
            {
                Game1.player.modData[MURPHY_SAILED_TODAY_KEY] = "false";
                Game1.player.modData[MURPHY_WAS_TRIP_SUCCESSFUL_KEY] = "false";
                Game1.player.modData[MURPHY_FINISHED_TALKING_KEY] = "false";
            }

            // One time event, do not renew
            if (!Game1.player.modData.ContainsKey(MURPHY_HAS_SEEN_FLAG_KEY))
            {
                Game1.player.modData.Add(MURPHY_HAS_SEEN_FLAG_KEY, "false");
            }
        }

        internal List<Farmer> GetFarmersOnTrawler()
        {
            return Game1.getAllFarmers().Where(f => IsValidTrawlerLocation(f.currentLocation)).ToList();
        }
    }
}
