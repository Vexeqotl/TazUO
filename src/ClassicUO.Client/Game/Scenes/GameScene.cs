﻿#region license

// Copyright (c) 2021, andreakarasho
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL2;
using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene : Scene
    {
        private static readonly Lazy<BlendState> _darknessBlend = new Lazy<BlendState>(() =>
        {
            BlendState state = new BlendState();
            state.ColorSourceBlend = Blend.Zero;
            state.ColorDestinationBlend = Blend.SourceColor;
            state.ColorBlendFunction = BlendFunction.Add;

            return state;
        });

        private static readonly Lazy<BlendState> _altLightsBlend = new Lazy<BlendState>(() =>
        {
            BlendState state = new BlendState();
            state.ColorSourceBlend = Blend.DestinationColor;
            state.ColorDestinationBlend = Blend.One;
            state.ColorBlendFunction = BlendFunction.Add;

            return state;
        });

        private uint _time_cleanup = Time.Ticks + 5000;
        private static XBREffect _xbr;
        private bool _alphaChanged;
        private long _alphaTimer;
        private bool _forceStopScene;
        private HealthLinesManager _healthLinesManager;

        private Point _lastSelectedMultiPositionInHouseCustomization;
        private int _lightCount;
        private readonly LightData[] _lights = new LightData[
            LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT
        ];
        private Item _multi;
        private Rectangle _rectangleObj = Rectangle.Empty,
            _rectanglePlayer;
        private long _timePing;

        private uint _timeToPlaceMultiInHouseCustomization;
        private readonly bool _use_render_target = false;
        private UseItemQueue _useItemQueue = new UseItemQueue();
        private MoveItemQueue _moveItemQueue = new MoveItemQueue();
        private bool _useObjectHandles;
        private RenderTarget2D _world_render_target,
            _lightRenderTarget;
        private AnimatedStaticsManager _animatedStaticsManager;

        public MoveItemQueue MoveItemQueue => _moveItemQueue;
        public bool UpdateDrawPosition { get; set; }
        public HotkeysManager Hotkeys { get; private set; }
        public MacroManager Macros { get; private set; }
        public InfoBarManager InfoBars { get; private set; }
        public Weather Weather { get; private set; }
        public bool DisconnectionRequested { get; set; }
        public bool UseLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseCustomLightLevel
                ? World.Light.Personal < World.Light.Overall
                : World.Light.RealPersonal < World.Light.RealOverall;
        public bool UseAltLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseAlternativeLights;

        private bool _followingMode
        {
            get { return ProfileManager.CurrentProfile.FollowingMode; }
            set { ProfileManager.CurrentProfile.FollowingMode = value; }
        }
        private uint _followingTarget
        {
            get { return ProfileManager.CurrentProfile.FollowingTarget; }
            set { ProfileManager.CurrentProfile.FollowingTarget = value; }
        }

        private uint _lastResync = Time.Ticks;

        public void DoubleClickDelayed(uint serial)
        {
            _useItemQueue.Add(serial);
        }

        public override void Load()
        {
            base.Load();

            UISettings.Preload();

            Client.Game.Window.AllowUserResizing = true;

            Camera.Zoom = ProfileManager.CurrentProfile.DefaultScale;
            Camera.Bounds.X = Math.Max(0, ProfileManager.CurrentProfile.GameWindowPosition.X);
            Camera.Bounds.Y = Math.Max(0, ProfileManager.CurrentProfile.GameWindowPosition.Y);
            Camera.Bounds.Width = Math.Max(0, ProfileManager.CurrentProfile.GameWindowSize.X);
            Camera.Bounds.Height = Math.Max(0, ProfileManager.CurrentProfile.GameWindowSize.Y);

            Client.Game.GameCursor.ItemHold.Clear();
            Hotkeys = new HotkeysManager();
            Macros = new MacroManager();
            Macros.Load();

            NameOverHeadManager.Load();

            _animatedStaticsManager = new AnimatedStaticsManager();
            _animatedStaticsManager.Initialize();
            InfoBars = new InfoBarManager();
            InfoBars.Load();
            _healthLinesManager = new HealthLinesManager();
            Weather = new Weather();

            WorldViewportGump viewport = new WorldViewportGump(this);
            UIManager.Add(viewport, false);

            if (!ProfileManager.CurrentProfile.TopbarGumpIsDisabled)
            {
                TopBarGump.Create();
            }

            CommandManager.Initialize();
            NetClient.Socket.Disconnected += SocketOnDisconnected;
            EventSink.MessageReceived += ChatOnMessageReceived;
            UIManager.ContainerScale = ProfileManager.CurrentProfile.ContainersScale / 100f;

            SDL.SDL_SetWindowMinimumSize(Client.Game.Window.Handle, 640, 480);

            if (ProfileManager.CurrentProfile.WindowBorderless)
            {
                Client.Game.SetWindowBorderless(true);
            }
            else if (Settings.GlobalSettings.IsWindowMaximized)
            {
                Client.Game.MaximizeWindow();
            }
            else if (Settings.GlobalSettings.WindowSize.HasValue)
            {
                int w = Settings.GlobalSettings.WindowSize.Value.X;
                int h = Settings.GlobalSettings.WindowSize.Value.Y;

                w = Math.Max(640, w);
                h = Math.Max(480, h);

                Client.Game.SetWindowSize(w, h);
            }

            CircleOfTransparency.Create(ProfileManager.CurrentProfile.CircleOfTransparencyRadius);
            Plugin.OnConnected();
            EventSink.InvokeOnConnected(null);
            GameController.UpdateBackgroundHueShader();
            SpellDefinition.LoadCustomSpells();
            SpellVisualRangeManager.Instance.OnSceneLoad();
            AutoLootManager.Instance.OnSceneLoad();

            foreach (var xml in ProfileManager.CurrentProfile.AutoOpenXmlGumps)
            {
                XmlGumpHandler.TryAutoOpenByName(xml);
            }

            LegionScripting.LegionScripting.Init();
            BuySellAgent.Load();
            GraphicsReplacement.Load();
        }

        private void ChatOnMessageReceived(object sender, MessageEventArgs e)
        {
            if (e.Type == MessageType.Command)
            {
                return;
            }

            string name;
            string text;

            ushort hue = e.Hue;

            switch (e.Type)
            {
                case MessageType.ChatSystem:
                    name = e.Name;
                    text = e.Text;
                    break;
                case MessageType.Regular:
                case MessageType.Limit3Spell:

                    if (e.Parent == null || !SerialHelper.IsValid(e.Parent.Serial))
                    {
                        name = ResGeneral.System;
                    }
                    else
                    {
                        name = e.Name;
                    }

                    text = e.Text;

                    break;

                case MessageType.System:
                    name =
                        string.IsNullOrEmpty(e.Name)
                        || string.Equals(
                            e.Name,
                            "system",
                            StringComparison.InvariantCultureIgnoreCase
                        )
                            ? ResGeneral.System
                            : e.Name;

                    text = e.Text;

                    break;

                case MessageType.Emote:
                    name = e.Name;
                    text = $"{e.Text}";

                    if (e.Hue == 0)
                    {
                        hue = ProfileManager.CurrentProfile.EmoteHue;
                    }

                    break;

                case MessageType.Label:

                    if (e.Parent == null || !SerialHelper.IsValid(e.Parent.Serial))
                    {
                        name = string.Empty;
                    }
                    else if (string.IsNullOrEmpty(e.Name))
                    {
                        name = ResGeneral.YouSee;
                    }
                    else
                    {
                        name = e.Name;
                    }

                    text = e.Text;

                    break;

                case MessageType.Spell:
                    name = e.Name;
                    text = e.Text;

                    break;

                case MessageType.Party:
                    text = e.Text;
                    name = string.Format(ResGeneral.Party0, e.Name);
                    hue = ProfileManager.CurrentProfile.PartyMessageHue;

                    break;

                case MessageType.Alliance:
                    text = e.Text;
                    name = string.Format(ResGeneral.Alliance0, e.Name);
                    hue = ProfileManager.CurrentProfile.AllyMessageHue;

                    break;

                case MessageType.Guild:
                    text = e.Text;
                    name = string.Format(ResGeneral.Guild0, e.Name);
                    hue = ProfileManager.CurrentProfile.GuildMessageHue;

                    break;

                default:
                    text = e.Text;
                    name = e.Name;
                    hue = e.Hue;

                    Log.Warn($"Unhandled text type {e.Type}  -  text: '{e.Text}'");

                    break;
            }

            if (!string.IsNullOrEmpty(text))
            {
                World.Journal.Add
                (
                    text,
                    hue,
                    name,
                    e.TextType,
                    e.IsUnicode,
                    e.Type
                );
            }
        }

        public override void Unload()
        {
            if (IsDestroyed)
            {
                return;
            }
            
            _moveItemQueue.Clear();

            GraphicsReplacement.Save();
            BuySellAgent.Unload();

            LegionScripting.LegionScripting.Unload();

            ProfileManager.CurrentProfile.GameWindowPosition = new Point(
                Camera.Bounds.X,
                Camera.Bounds.Y
            );
            ProfileManager.CurrentProfile.GameWindowSize = new Point(
                Camera.Bounds.Width,
                Camera.Bounds.Height
            );
            ProfileManager.CurrentProfile.DefaultScale = Camera.Zoom;

            Client.Game.Audio?.StopMusic();
            Client.Game.Audio?.StopSounds();

            Client.Game.SetWindowTitle(string.Empty);
            Client.Game.GameCursor.ItemHold.Clear();

            try
            {
                Plugin.OnDisconnected();
            }
            catch { }

            EventSink.InvokeOnDisconnected(null);

            TargetManager.Reset();

            // special case for wmap. this allow us to save settings
            UIManager.GetGump<WorldMapGump>()?.SaveSettings();

            ProfileManager.CurrentProfile?.Save(ProfileManager.ProfilePath);
            TileMarkerManager.Instance.Save();
            SpellVisualRangeManager.Instance.Save();
            SpellVisualRangeManager.Instance.OnSceneUnload();
            AutoLootManager.Instance.OnSceneUnload();

            NameOverHeadManager.Save();

            Macros.Save();
            InfoBars.Save();
            ProfileManager.UnLoadProfile();

            StaticFilters.CleanTreeTextures();

            NetClient.Socket.Disconnected -= SocketOnDisconnected;
            NetClient.Socket.Disconnect();
            _lightRenderTarget?.Dispose();
            _world_render_target?.Dispose();

            CommandManager.UnRegisterAll();
            Weather.Reset();
            SkillProgressBar.QueManager.Reset();
            UIManager.Clear();
            World.Clear();
            ChatManager.Clear();
            DelayedObjectClickManager.Clear();

            _useItemQueue?.Clear();
            _useItemQueue = null;
            Hotkeys = null;
            Macros = null;
            EventSink.MessageReceived -= ChatOnMessageReceived;

            Settings.GlobalSettings.WindowSize = new Point(
                Client.Game.Window.ClientBounds.Width,
                Client.Game.Window.ClientBounds.Height
            );

            Settings.GlobalSettings.IsWindowMaximized = Client.Game.IsWindowMaximized();
            Client.Game.SetWindowBorderless(false);

            base.Unload();
        }

        private void SocketOnDisconnected(object sender, SocketError e)
        {
            if (Settings.GlobalSettings.Reconnect)
            {
                _forceStopScene = true;
            }
            else
            {
                UIManager.Add(
                    new MessageBoxGump(
                        200,
                        200,
                        string.Format(
                            ResGeneral.ConnectionLost0,
                            StringHelper.AddSpaceBeforeCapital(e.ToString())
                        ),
                        s =>
                        {
                            if (s)
                            {
                                Client.Game.SetScene(new LoginScene());
                            }
                        }
                    )
                );
            }
        }

        public void RequestQuitGame()
        {
            UIManager.Add(
                new QuestionGump(
                    ResGeneral.QuitPrompt,
                    s =>
                    {
                        if (s)
                        {
                            if (
                                (
                                    World.ClientFeatures.Flags
                                    & CharacterListFlags.CLF_OWERWRITE_CONFIGURATION_BUTTON
                                ) != 0
                            )
                            {
                                DisconnectionRequested = true;
                                NetClient.Socket.Send_LogoutNotification();
                            }
                            else
                            {
                                NetClient.Socket.Disconnect();
                                Client.Game.SetScene(new LoginScene());
                            }
                        }
                    }
                )
            );
        }

        public void AddLight(GameObject obj, GameObject lightObject, int x, int y)
        {
            if (
                _lightCount >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT
                || !UseLights && !UseAltLights
                || obj == null
            )
            {
                return;
            }

            bool canBeAdded = true;

            int testX = obj.X + 1;
            int testY = obj.Y + 1;

            GameObject tile = World.Map.GetTile(testX, testY);

            if (tile != null)
            {
                sbyte z5 = (sbyte)(obj.Z + 5);

                for (GameObject o = tile; o != null; o = o.TNext)
                {
                    if (
                        (!(o is Static s) || s.ItemData.IsTransparent)
                            && (!(o is Multi m) || m.ItemData.IsTransparent)
                        || !o.AllowedToDraw
                    )
                    {
                        continue;
                    }

                    if (o.Z < _maxZ && o.Z >= z5)
                    {
                        canBeAdded = false;

                        break;
                    }
                }
            }

            if (canBeAdded)
            {
                ref LightData light = ref _lights[_lightCount];

                ushort graphic = lightObject.Graphic;

                if (
                    graphic >= 0x3E02 && graphic <= 0x3E0B
                    || graphic >= 0x3914 && graphic <= 0x3929
                    || graphic == 0x0B1D
                )
                {
                    light.ID = 2;
                }
                else
                {
                    if (obj == lightObject && obj is Item item)
                    {
                        light.ID = item.LightID;
                    }
                    else if (lightObject is Item it)
                    {
                        light.ID = (byte)it.ItemData.LightIndex;

                        if (obj is Mobile mob)
                        {
                            switch (mob.Direction)
                            {
                                case Direction.Right:
                                    y += 33;
                                    x += 22;

                                    break;

                                case Direction.Left:
                                    y += 33;
                                    x -= 22;

                                    break;

                                case Direction.East:
                                    x += 22;
                                    y += 55;

                                    break;

                                case Direction.Down:
                                    y += 55;

                                    break;

                                case Direction.South:
                                    x -= 22;
                                    y += 55;

                                    break;
                            }
                        }
                    }
                    else if (obj is Mobile _)
                    {
                        light.ID = 1;
                    }
                    else
                    {
                        ref StaticTiles data = ref TileDataLoader.Instance.StaticData[obj.Graphic];
                        light.ID = data.Layer;
                    }
                }

                light.Color = 0;
                light.IsHue = false;

                if (ProfileManager.CurrentProfile.UseColoredLights)
                {
                    if (light.ID > 200)
                    {
                        light.Color = (ushort)(light.ID - 200);
                        light.ID = 1;
                    }

                    if (LightColors.GetHue(graphic, out ushort color, out bool ishue))
                    {
                        light.Color = color;
                        light.IsHue = ishue;
                    }
                }

                if (light.ID >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT)
                {
                    return;
                }

                if (light.Color != 0)
                {
                    light.Color++;
                }

                light.DrawX = x;
                light.DrawY = y;
                _lightCount++;
            }
        }

        private void FillGameObjectList()
        {
            _renderListStaticsHead = null;
            _renderList = null;
            _renderListStaticsCount = 0;

            _renderListTransparentObjectsHead = null;
            _renderListTransparentObjects = null;
            _renderListTransparentObjectsCount = 0;

            _renderListAnimationsHead = null;
            _renderListAnimations = null;
            _renderListAnimationCount = 0;

            _renderListEffectsHead = null;
            _renderListEffects = null;
            _renderListEffectCount = 0;

            _foliageCount = 0;

            if (!World.InGame)
            {
                return;
            }

            _alphaChanged = _alphaTimer < Time.Ticks;

            if (_alphaChanged)
            {
                _alphaTimer = Time.Ticks + Constants.ALPHA_TIME;
            }

            FoliageIndex++;

            if (FoliageIndex >= 100)
            {
                FoliageIndex = 1;
            }

            GetViewPort();

            var useObjectHandles = NameOverHeadManager.IsShowing;
            if (useObjectHandles != _useObjectHandles)
            {
                _useObjectHandles = useObjectHandles;
                if (_useObjectHandles)
                {
                    NameOverHeadManager.Open();

                }
                else
                {
                    NameOverHeadManager.Close();
                }
            }

            _rectanglePlayer.X = (int)(
                World.Player.RealScreenPosition.X
                - World.Player.FrameInfo.X
                + 22
                + World.Player.Offset.X
            );
            _rectanglePlayer.Y = (int)(
                World.Player.RealScreenPosition.Y
                - World.Player.FrameInfo.Y
                + 22
                + (World.Player.Offset.Y - World.Player.Offset.Z)
            );
            _rectanglePlayer.Width = World.Player.FrameInfo.Width;
            _rectanglePlayer.Height = World.Player.FrameInfo.Height;

            int minX = _minTile.X;
            int minY = _minTile.Y;
            int maxX = _maxTile.X;
            int maxY = _maxTile.Y;
            Map.Map map = World.Map;
            bool use_handles = _useObjectHandles;
            int maxCotZ = World.Player.Z + 5;
            Vector2 playerPos = World.Player.GetScreenPosition();

            for (int i = 0; i < 2; ++i)
            {
                int minValue = minY;
                int maxValue = maxY;

                if (i != 0)
                {
                    minValue = minX;
                    maxValue = maxX;
                }

                for (int lead = minValue; lead < maxValue; ++lead)
                {
                    int x = minX;
                    int y = lead;

                    if (i != 0)
                    {
                        x = lead;
                        y = maxY;
                    }

                    while (x >= minX && x <= maxX && y >= minY && y <= maxY)
                    {
                        AddTileToRenderList(
                            map.GetTile(x, y),
                            x,
                            y,
                            use_handles,
                            150,
                            maxCotZ,
                            ref playerPos
                        );

                        ++x;
                        --y;
                    }
                }
            }

            if (_alphaChanged)
            {
                for (int i = 0; i < _foliageCount; i++)
                {
                    GameObject f = _foliages[i];

                    if (f.FoliageIndex == FoliageIndex)
                    {
                        CalculateAlpha(ref f.AlphaHue, Constants.FOLIAGE_ALPHA);
                    }
                    else if (f.Z < _maxZ)
                    {
                        CalculateAlpha(ref f.AlphaHue, 0xFF);
                    }
                }
            }

            UpdateTextServerEntities(World.Mobiles.Values, true);
            UpdateTextServerEntities(World.Items.Values, false);

            UpdateDrawPosition = false;
        }

        private void UpdateTextServerEntities<T>(IEnumerable<T> entities, bool force)
            where T : Entity
        {
            foreach (T e in entities)
            {
                if (
                    e.TextContainer != null
                    && !e.TextContainer.IsEmpty
                    && (force || e.Graphic == 0x2006)
                )
                {
                    e.UpdateRealScreenPosition(_offset.X, _offset.Y);
                }
            }
        }

        public override void Update()
        {
            Profile currentProfile = ProfileManager.CurrentProfile;

            SelectedObject.TranslatedMousePositionByViewport = Camera.MouseToWorldPosition();

            base.Update();

            if (_time_cleanup < Time.Ticks)
            {
                World.Map?.ClearUnusedBlocks();
                _time_cleanup = Time.Ticks + 500;
            }

            PacketHandlers.SendMegaClilocRequests();

            if (_forceStopScene)
            {
                LoginScene loginScene = new LoginScene();
                Client.Game.SetScene(loginScene);
                loginScene.Reconnect = true;

                return;
            }

            if (!World.InGame)
            {
                return;
            }

            if (Time.Ticks > _timePing)
            {
                NetClient.Socket.Statistics.SendPing();
                _timePing = (long)Time.Ticks + 1000;
            }

            if (currentProfile.ForceResyncOnHang && Time.Ticks - NetClient.Socket.Statistics.LastPingReceived > 5000 && Time.Ticks - _lastResync > 5000)
            {
                //Last ping > ~5 seconds
                NetClient.Socket.Send_Resync();
                _lastResync = Time.Ticks;
                GameActions.Print("Possible connection hang, resync attempted", 32, MessageType.System);
            }

            World.Update();
            _animatedStaticsManager.Process();
            BoatMovingManager.Update();
            Pathfinder.ProcessAutoWalk();
            DelayedObjectClickManager.Update();
            AutoLootManager.Instance.Update();
            _moveItemQueue.ProcessQueue();

            if (!MoveCharacterByMouseInput() && !currentProfile.DisableArrowBtn && !MoveCharByController())
            {
                Direction dir = DirectionHelper.DirectionFromKeyboardArrows(
                    _flags[0],
                    _flags[2],
                    _flags[1],
                    _flags[3]
                );

                if (World.InGame && !Pathfinder.AutoWalking && dir != Direction.NONE)
                {
                    World.Player.Walk(dir, currentProfile.AlwaysRun);
                }
            }

            if (currentProfile.FollowingMode && SerialHelper.IsMobile(currentProfile.FollowingTarget) && !Pathfinder.AutoWalking)
            {
                Mobile follow = World.Mobiles.Get(currentProfile.FollowingTarget);

                if (follow != null)
                {
                    int distance = follow.Distance;

                    if (distance > World.ClientViewRange)
                    {
                        StopFollowing();
                    }
                    else if (distance > currentProfile.AutoFollowDistance)
                    {
                        if (!Pathfinder.WalkTo(follow.X, follow.Y, follow.Z, currentProfile.AutoFollowDistance) && !World.Player.IsParalyzed)
                        {
                            StopFollowing(); //Can't get there
                        }
                    }
                }
                else
                {
                    StopFollowing();
                }
            }

            Macros.Update();

            if (
                (currentProfile.CorpseOpenOptions == 1 || currentProfile.CorpseOpenOptions == 3)
                    && TargetManager.IsTargeting
                || (currentProfile.CorpseOpenOptions == 2 || currentProfile.CorpseOpenOptions == 3)
                    && World.Player.IsHidden
            )
            {
                _useItemQueue.ClearCorpses();
            }

            _useItemQueue.Update();

            if (!UIManager.IsMouseOverWorld)
            {
                SelectedObject.Object = null;
            }

            if (
                TargetManager.IsTargeting
                && TargetManager.TargetingState == CursorTarget.MultiPlacement
                && World.CustomHouseManager == null
                && TargetManager.MultiTargetInfo != null
            )
            {
                if (_multi == null)
                {
                    _multi = Item.Create(0);
                    _multi.Graphic = TargetManager.MultiTargetInfo.Model;
                    _multi.Hue = TargetManager.MultiTargetInfo.Hue;
                    _multi.IsMulti = true;
                }

                if (SelectedObject.Object is GameObject gobj)
                {
                    ushort x,
                        y;
                    sbyte z;

                    int cellX = gobj.X % 8;
                    int cellY = gobj.Y % 8;

                    GameObject o = World.Map.GetChunk(gobj.X, gobj.Y)?.Tiles[cellX, cellY];

                    if (o != null)
                    {
                        x = o.X;
                        y = o.Y;
                        z = o.Z;
                    }
                    else
                    {
                        x = gobj.X;
                        y = gobj.Y;
                        z = gobj.Z;
                    }

                    World.Map.GetMapZ(x, y, out sbyte groundZ, out sbyte _);

                    if (gobj is Static st && st.ItemData.IsWet)
                    {
                        groundZ = gobj.Z;
                    }

                    x = (ushort)(x - TargetManager.MultiTargetInfo.XOff);
                    y = (ushort)(y - TargetManager.MultiTargetInfo.YOff);
                    z = (sbyte)(groundZ - TargetManager.MultiTargetInfo.ZOff);

                    _multi.SetInWorldTile(x, y, z);
                    _multi.CheckGraphicChange();

                    World.HouseManager.TryGetHouse(_multi.Serial, out House house);

                    foreach (Multi s in house.Components)
                    {
                        s.IsHousePreview = true;
                        s.SetInWorldTile(
                            (ushort)(_multi.X + s.MultiOffsetX),
                            (ushort)(_multi.Y + s.MultiOffsetY),
                            (sbyte)(_multi.Z + s.MultiOffsetZ)
                        );
                    }
                }
            }
            else if (_multi != null)
            {
                World.HouseManager.RemoveMultiTargetHouse();
                _multi.Destroy();
                _multi = null;
            }

            if (_isMouseLeftDown && !Client.Game.GameCursor.ItemHold.Enabled)
            {
                if (
                    World.CustomHouseManager != null
                    && World.CustomHouseManager.SelectedGraphic != 0
                    && !World.CustomHouseManager.SeekTile
                    && !World.CustomHouseManager.Erasing
                    && Time.Ticks > _timeToPlaceMultiInHouseCustomization
                )
                {
                    if (
                        SelectedObject.Object is GameObject obj
                        && (
                            obj.X != _lastSelectedMultiPositionInHouseCustomization.X
                            || obj.Y != _lastSelectedMultiPositionInHouseCustomization.Y
                        )
                    )
                    {
                        World.CustomHouseManager.OnTargetWorld(obj);
                        _timeToPlaceMultiInHouseCustomization = Time.Ticks + 50;
                        _lastSelectedMultiPositionInHouseCustomization.X = obj.X;
                        _lastSelectedMultiPositionInHouseCustomization.Y = obj.Y;
                    }
                }
                else if (Time.Ticks - _holdMouse2secOverItemTime >= 1000)
                {
                    if (SelectedObject.Object is Item it && GameActions.PickUp(it.Serial, 0, 0))
                    {
                        _isMouseLeftDown = false;
                        _holdMouse2secOverItemTime = 0;
                    }
                }
            }
        }

        public override bool Draw(UltimaBatcher2D batcher)
        {
            if (!World.InGame)
            {
                return false;
            }

            if (CheckDeathScreen(batcher))
            {
                return true;
            }

            Viewport r_viewport = batcher.GraphicsDevice.Viewport;
            Viewport camera_viewport = Camera.GetViewport();
            Matrix matrix = _use_render_target ? Matrix.Identity : Camera.ViewTransformMatrix;

            bool can_draw_lights = false;

            if (!_use_render_target)
            {
                if (ProfileManager.CurrentProfile.GlobalScaling)
                {
                    Camera.Zoom = 1f; // oScale + ProfileManager.CurrentProfile.GlobalScale;
                    matrix = Matrix.CreateScale(ProfileManager.CurrentProfile.GlobalScale);
                    camera_viewport.Bounds = new Rectangle(
                        (int)(camera_viewport.Bounds.X * ProfileManager.CurrentProfile.GlobalScale),
                        (int)(camera_viewport.Bounds.Y * ProfileManager.CurrentProfile.GlobalScale),
                        (int)(camera_viewport.Bounds.Width * ProfileManager.CurrentProfile.GlobalScale),
                        (int)(camera_viewport.Bounds.Height * ProfileManager.CurrentProfile.GlobalScale)
                        );
                }

                can_draw_lights = PrepareLightsRendering(batcher, ref matrix);
                batcher.GraphicsDevice.Viewport = camera_viewport;
            }

            DrawWorld(batcher, ref matrix, _use_render_target);

            if (_use_render_target)
            {
                can_draw_lights = PrepareLightsRendering(batcher, ref matrix);
                batcher.GraphicsDevice.Viewport = camera_viewport;
            }

            // draw world rt
            Vector3 hue = Vector3.Zero;
            hue.Z = 1f;

            if (_use_render_target)
            {
                //switch (ProfileManager.CurrentProfile.FilterType)
                //{
                //    default:
                //    case 0:
                //        batcher.SetSampler(SamplerState.PointClamp);
                //        break;
                //    case 1:
                //        batcher.SetSampler(SamplerState.AnisotropicClamp);
                //        break;
                //    case 2:
                //        batcher.SetSampler(SamplerState.LinearClamp);
                //        break;
                //}

                if (_xbr == null)
                {
                    _xbr = new XBREffect(batcher.GraphicsDevice);
                }

                _xbr.TextureSize.SetValue(new Vector2(Camera.Bounds.Width, Camera.Bounds.Height));

                //Point p = Point.Zero;

                //p = Camera.ScreenToWorld(p);
                //int minPixelsX = p.X;
                //int minPixelsY = p.Y;

                //p.X = Camera.Bounds.Width;
                //p.Y = Camera.Bounds.Height;
                //p = Camera.ScreenToWorld(p);
                //int maxPixelsX = p.X;
                //int maxPixelsY = p.Y;

                batcher.Begin(null, Camera.ViewTransformMatrix);

                batcher.Draw(
                    _world_render_target,
                    new Rectangle(0, 0, Camera.Bounds.Width, Camera.Bounds.Height),
                    hue
                );

                batcher.End();

                //batcher.SetSampler(null);
            }

            // draw lights
            if (can_draw_lights)
            {
                if (ProfileManager.CurrentProfile.GlobalScaling)
                    batcher.Begin(null, Matrix.CreateScale(ProfileManager.CurrentProfile.GlobalScale));
                else
                    batcher.Begin();

                if (UseAltLights)
                {
                    hue.Z = .5f;
                    batcher.SetBlendState(_altLightsBlend.Value);
                }
                else
                {
                    batcher.SetBlendState(_darknessBlend.Value);
                }

                batcher.Draw(
                    _lightRenderTarget,
                    new Rectangle(0, 0, Camera.Bounds.Width, Camera.Bounds.Height),
                    hue
                );

                batcher.SetBlendState(null);
                batcher.End();

                hue.Z = 1f;
            }

            if (ProfileManager.CurrentProfile.GlobalScaling)
                batcher.Begin(null, Matrix.CreateScale(ProfileManager.CurrentProfile.GlobalScale));
            else
                batcher.Begin();
            DrawOverheads(batcher);
            DrawSelection(batcher);
            batcher.End();

            batcher.GraphicsDevice.Viewport = r_viewport;

            return base.Draw(batcher);
        }

        private void DrawWorld(UltimaBatcher2D batcher, ref Matrix matrix, bool use_render_target)
        {
            SelectedObject.Object = null;
            FillGameObjectList();

            if (use_render_target)
            {
                batcher.GraphicsDevice.SetRenderTarget(_world_render_target);
                batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Black, 0f, 0);
            }
            else
            {
                batcher.SetSampler(SamplerState.PointClamp);
            }

            batcher.Begin(null, matrix);
            batcher.SetBrightlight(ProfileManager.CurrentProfile.TerrainShadowsLevel * 0.1f);

            // https://shawnhargreaves.com/blog/depth-sorting-alpha-blended-objects.html
            batcher.SetStencil(DepthStencilState.Default);

            RenderedObjectsCount = 0;
            RenderedObjectsCount += DrawRenderList(
                batcher,
                _renderListStaticsHead,
                _renderListStaticsCount
            );
            RenderedObjectsCount += DrawRenderList(
                batcher,
                _renderListAnimationsHead,
                _renderListAnimationCount
            );
            RenderedObjectsCount += DrawRenderList(
                batcher,
                _renderListEffectsHead,
                _renderListEffectCount
            );

            if (_renderListTransparentObjectsCount > 0)
            {
                batcher.SetStencil(DepthStencilState.DepthRead);
                RenderedObjectsCount += DrawRenderList(
                    batcher,
                    _renderListTransparentObjectsHead,
                    _renderListTransparentObjectsCount
                );
            }

            batcher.SetStencil(null);

            //var worldPoint = Camera.MouseToWorldPosition() + _offset;
            //worldPoint.X += 22;
            //worldPoint.Y += 22;

            //var isoX = (int)(0.5f * (worldPoint.X / 22f + worldPoint.Y / 22f));
            //var isoY = (int)(0.5f * (-worldPoint.X / 22f + worldPoint.Y / 22f));

            //GameObject selectedObject = World.Map.GetTile(isoX, isoY, false);

            //if (selectedObject != null)
            //{
            //    selectedObject.Hue = 0x44;
            //}


            if (
                _multi != null
                && TargetManager.IsTargeting
                && TargetManager.TargetingState == CursorTarget.MultiPlacement
            )
            {
                _multi.Draw(
                    batcher,
                    _multi.RealScreenPosition.X,
                    _multi.RealScreenPosition.Y,
                    _multi.CalculateDepthZ()
                );
            }

            batcher.SetSampler(null);
            batcher.SetStencil(null);

            // draw weather
            Weather.Draw(batcher, 0, 0); // TODO: fix the depth

            batcher.End();

            int flushes = batcher.FlushesDone;
            int switches = batcher.TextureSwitches;

            if (use_render_target)
            {
                batcher.GraphicsDevice.SetRenderTarget(null);
            }

            //batcher.Begin();
            //hueVec.X = 0;
            //hueVec.Y = 1;
            //hueVec.Z = 1;
            //string s = $"Flushes: {flushes}\nSwitches: {switches}\nArt texture count: {TextureAtlas.Shared.TexturesCount}\nMaxZ: {_maxZ}\nMaxGround: {_maxGroundZ}";
            //batcher.DrawString(Fonts.Bold, s, 200, 200, ref hueVec);
            //hueVec = Vector3.Zero;
            //batcher.DrawString(Fonts.Bold, s, 200 + 1, 200 - 1, ref hueVec);
            //batcher.End();
        }

        private int DrawRenderList(UltimaBatcher2D batcher, GameObject obj, int count)
        {
            int done = 0;

            for (int i = 0; i < count; obj = obj.RenderListNext, ++i)
            {
                if (obj.Z <= _maxGroundZ)
                {
                    float depth = obj.CalculateDepthZ();

                    if (
                        obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth)
                    )
                    {
                        ++done;
                    }
                }
            }

            return done;
        }

        private bool PrepareLightsRendering(UltimaBatcher2D batcher, ref Matrix matrix)
        {
            if (
                !UseLights && !UseAltLights
                || World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect
                || _lightRenderTarget == null
            )
            {
                return false;
            }

            batcher.GraphicsDevice.SetRenderTarget(_lightRenderTarget);
            batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Black, 0f, 0);

            if (!UseAltLights)
            {
                float lightColor = World.Light.IsometricLevel;

                if (ProfileManager.CurrentProfile.UseDarkNights)
                {
                    lightColor -= 0.04f;
                }

                batcher.GraphicsDevice.Clear(
                    ClearOptions.Target,
                    new Vector4(lightColor, lightColor, lightColor, 1),
                    0f,
                    0
                );
            }

            batcher.Begin(null, matrix);
            batcher.SetBlendState(BlendState.Additive);

            Vector3 hue = Vector3.Zero;

            hue.Z = 1f;

            for (int i = 0; i < _lightCount; i++)
            {
                ref LightData l = ref _lights[i];
                ref readonly var lightInfo = ref Client.Game.Lights.GetLight(l.ID);

                if (lightInfo.Texture == null)
                {
                    continue;
                }

                hue.X = l.Color;
                hue.Y =
                    hue.X > 1.0f
                        ? l.IsHue
                            ? ShaderHueTranslator.SHADER_HUED
                            : ShaderHueTranslator.SHADER_LIGHTS
                        : ShaderHueTranslator.SHADER_NONE;

                batcher.Draw(
                    lightInfo.Texture,
                    new Vector2(
                        l.DrawX - lightInfo.UV.Width * 0.5f,
                        l.DrawY - lightInfo.UV.Height * 0.5f
                    ),
                    lightInfo.UV,
                    hue
                );
            }

            _lightCount = 0;

            batcher.SetBlendState(null);
            batcher.End();

            batcher.GraphicsDevice.SetRenderTarget(null);

            return true;
        }

        public void DrawOverheads(UltimaBatcher2D batcher)
        {
            _healthLinesManager.Draw(batcher);

            if (!UIManager.IsMouseOverWorld)
            {
                SelectedObject.Object = null;
            }

            World.WorldTextManager.ProcessWorldText(true);
            World.WorldTextManager.Draw(batcher, Camera.Bounds.X, Camera.Bounds.Y);
        }

        public void DrawSelection(UltimaBatcher2D batcher)
        {
            if (_isSelectionActive)
            {
                Vector3 selectionHue = new Vector3();
                selectionHue.Z = 0.7f;

                int minX = Math.Min(_selectionStart.X, Mouse.Position.X);
                int maxX = Math.Max(_selectionStart.X, Mouse.Position.X);
                int minY = Math.Min(_selectionStart.Y, Mouse.Position.Y);
                int maxY = Math.Max(_selectionStart.Y, Mouse.Position.Y);

                Rectangle selectionRect = new Rectangle(
                    minX - Camera.Bounds.X,
                    minY - Camera.Bounds.Y,
                    maxX - minX,
                    maxY - minY
                );

                batcher.Draw(
                    SolidColorTextureCache.GetTexture(Color.Black),
                    selectionRect,
                    selectionHue
                );

                selectionHue.Z = 0.3f;

                batcher.DrawRectangle(
                    SolidColorTextureCache.GetTexture(Color.DeepSkyBlue),
                    selectionRect.X,
                    selectionRect.Y,
                    selectionRect.Width,
                    selectionRect.Height,
                    selectionHue
                );
            }
        }

        private static readonly RenderedText _youAreDeadText = RenderedText.Create(
            ResGeneral.YouAreDead,
            0xFFFF,
            3,
            false,
            FontStyle.BlackBorder,
            TEXT_ALIGN_TYPE.TS_LEFT
        );

        private bool CheckDeathScreen(UltimaBatcher2D batcher)
        {
            if (
                ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.EnableDeathScreen
            )
            {
                if (World.InGame)
                {
                    if (World.Player.IsDead && World.Player.DeathScreenTimer > Time.Ticks)
                    {
                        batcher.Begin();
                        _youAreDeadText.Draw(
                            batcher,
                            Camera.Bounds.X + (Camera.Bounds.Width / 2 - _youAreDeadText.Width / 2),
                            Camera.Bounds.Bottom / 2
                        );
                        batcher.End();

                        return true;
                    }
                }
            }

            return false;
        }

        private void StopFollowing()
        {
            if (ProfileManager.CurrentProfile.FollowingMode)
            {
                ProfileManager.CurrentProfile.FollowingMode = false;
                ProfileManager.CurrentProfile.FollowingTarget = 0;
                Pathfinder.StopAutoWalk();

                MessageManager.HandleMessage(
                    World.Player,
                    ResGeneral.StoppedFollowing,
                    string.Empty,
                    0,
                    MessageType.Regular,
                    3,
                    TextType.CLIENT
                );
            }
        }

        private struct LightData
        {
            public byte ID;
            public ushort Color;
            public bool IsHue;
            public int DrawX,
                DrawY;
        }
    }
}
