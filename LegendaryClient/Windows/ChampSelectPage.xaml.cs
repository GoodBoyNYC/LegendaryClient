﻿using jabber.connection;
using LegendaryClient.Controls;
using LegendaryClient.Logic;
using LegendaryClient.Logic.PlayerSpell;
using LegendaryClient.Logic.SQLite;
using PVPNetConnect.RiotObjects.Platform.Catalog.Champion;
using PVPNetConnect.RiotObjects.Platform.Game;
using PVPNetConnect.RiotObjects.Platform.Reroll.Pojo;
using PVPNetConnect.RiotObjects.Platform.Summoner.Masterybook;
using PVPNetConnect.RiotObjects.Platform.Summoner.Spellbook;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LegendaryClient.Windows
{
    /// <summary>
    /// Interaction logic for ChampSelectPage.xaml
    /// </summary>
    public partial class ChampSelectPage : Page
    {
        private bool _BanningPhase;
        private bool BanningPhase
        {
            get { return _BanningPhase; }
            set {
                if (_BanningPhase != value)
                {
                    RenderChamps(value);
                    _BanningPhase = value;
                }
            }
        }

        private int _LastPickTurn;
        private int LastPickTurn
        {
            get { return _LastPickTurn; }
            set
            {
                if (_LastPickTurn != value)
                {
                    counter = configType.MainPickTimerDuration - 3;
                    _LastPickTurn = value;
                }
            }
        }

        private GameDTO LatestDto;
        private List<ChampionDTO> ChampList;
        private List<ChampionBanInfoDTO> ChampionsForBan;
        private MasteryBookDTO MyMasteries;
        private SpellBookDTO MyRunes;
        private GameTypeConfigDTO configType;
        private System.Windows.Forms.Timer CountdownTimer;
        private int counter;
        private bool HasLockedIn = false;
        private bool DevMode = false;
        private bool HasLaunchedGame = false;
        private bool QuickLoad = false; //Don't load masteries and runes on load at start
        private Room Chatroom;

        public ChampSelectPage()
        {
            InitializeComponent();
            StartChampSelect();
        }

        private async void StartChampSelect()
        {
            Client.FocusClient();
            ChampList = new List<ChampionDTO>(Client.PlayerChampions);
            ChampList.Sort((x, y) => champions.GetChampion(x.ChampionId).displayName.CompareTo(champions.GetChampion(y.ChampionId).displayName));
            MyMasteries = Client.LoginPacket.AllSummonerData.MasteryBook;
            MyRunes = Client.LoginPacket.AllSummonerData.SpellBook;

            int i = 0;
            foreach (MasteryBookPageDTO MasteryPage in MyMasteries.BookPages)
            {
                string MasteryPageName = MasteryPage.Name;
                if (MasteryPageName.StartsWith("@@"))
                {
                    MasteryPageName = "Mastery Page " + ++i;
                }
                MasteryComboBox.Items.Add(MasteryPageName);
                if (MasteryPage.Current)
                    MasteryComboBox.SelectedValue = MasteryPageName;
            }
            i = 0;
            foreach (SpellBookPageDTO RunePage in MyRunes.BookPages)
            {
                string RunePageName = RunePage.Name;
                if (RunePageName.StartsWith("@@"))
                {
                    RunePageName = "Rune Page " + ++i;
                }
                RuneComboBox.Items.Add(RunePageName);
                if (RunePage.Current)
                    RuneComboBox.SelectedValue = RunePageName;
            }

            QuickLoad = true;

            await Client.PVPNet.SetClientReceivedGameMessage(Client.GameID, "CHAMP_SELECT_CLIENT");
            GameDTO latestDTO = await Client.PVPNet.GetLatestGameTimerState(Client.GameID, Client.ChampSelectDTO.GameState, Client.ChampSelectDTO.PickTurn);
            configType = Client.LoginPacket.GameTypeConfigs.Find(x => x.Id == latestDTO.GameTypeConfigId);
            if (configType == null) //Invalid config... abort!
            {
                Client.PVPNet.OnMessageReceived -= ChampSelect_OnMessageReceived;
                await Client.PVPNet.QuitGame();

                Client.SwitchPage(new MainPage());
                MessageOverlay overlay = new MessageOverlay();
                overlay.MessageTextBox.Text = "Invalid Config ID (" + latestDTO.GameTypeConfigId.ToString() + "). Report to Snowl [https://github.com/Snowl/LegendaryClient/issues/new]";
                overlay.MessageTitle.Content = "Invalid Config";
                Client.OverlayContainer.Content = overlay.Content;
                Client.OverlayContainer.Visibility = Visibility.Visible;
                return;
            }
            counter = configType.MainPickTimerDuration - 5; //Seems to be a 5 second inconsistancy with riot and what they actually provide
            CountdownTimer = new System.Windows.Forms.Timer();
            CountdownTimer.Tick += new EventHandler(CountdownTimer_Tick);
            CountdownTimer.Interval = 1000; // 1 second
            CountdownTimer.Start();

            LatestDto = latestDTO;
            ChampionBanInfoDTO[] ChampsForBan = await Client.PVPNet.GetChampionsForBan();

            string JID = Client.GetChatroomJID(latestDTO.RoomName.Replace("@sec", ""), latestDTO.RoomPassword, false);
            Chatroom = Client.ConfManager.GetRoom(new jabber.JID(JID));
            Chatroom.Nickname = Client.LoginPacket.AllSummonerData.Summoner.Name;
            Chatroom.OnRoomMessage += Chatroom_OnRoomMessage;
            Chatroom.OnParticipantJoin += Chatroom_OnParticipantJoin;
            Chatroom.Join(latestDTO.RoomPassword);

            ChampionsForBan = new List<ChampionBanInfoDTO>(ChampsForBan);
            ChampionsForBan.Sort((x, y) => champions.GetChampion(x.ChampionId).displayName.CompareTo(champions.GetChampion(y.ChampionId).displayName));
            RenderChamps(false);

            ChampSelect_OnMessageReceived(this, latestDTO);
            Client.PVPNet.OnMessageReceived += ChampSelect_OnMessageReceived;
        }

        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            counter--;
            if (counter <= 0)
                return;
            LobbyTimeLabel.Content = counter;
        }

        private void ChampSelect_OnMessageReceived(object sender, object message)
        {
            if (message.GetType() == typeof(GameDTO))
            {
                #region In Champion Select

                GameDTO ChampDTO = message as GameDTO;
                LatestDto = ChampDTO;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new ThreadStart(() =>
                {
                    ListViewItem[] ChampionArray = new ListViewItem[ChampionSelectListView.Items.Count];
                    ChampionSelectListView.Items.CopyTo(ChampionArray, 0);
                    foreach (ListViewItem y in ChampionArray)
                    {
                        y.IsHitTestVisible = true;
                        y.Opacity = 1;
                    }

                    List<Participant> AllParticipants = new List<Participant>(ChampDTO.TeamOne.ToArray());
                    AllParticipants.AddRange(ChampDTO.TeamTwo);
                    foreach (Participant p in AllParticipants)
                    {
                        if (p is PlayerParticipant)
                        {
                            PlayerParticipant play = (PlayerParticipant)p;
                            if (play.PickTurn == ChampDTO.PickTurn)
                            {
                                if (play.SummonerId == Client.LoginPacket.AllSummonerData.Summoner.SumId)
                                {
                                    ChampionSelectListView.IsHitTestVisible = true;
                                    ChampionSelectListView.Opacity = 1;
                                    GameStatusLabel.Content = "Your turn to pick!";
                                    break;
                                }
                            }
                        }
                        if (!DevMode)
                        {
                            ChampionSelectListView.IsHitTestVisible = false;
                            ChampionSelectListView.Opacity = 0.5;
                        }
                        GameStatusLabel.Content = "Waiting for others to pick...";
                    }

                    if (ChampDTO.GameState == "TEAM_SELECT")
                    {
                        if (CountdownTimer != null)
                        {
                            CountdownTimer.Stop();
                        }
                        Client.PVPNet.OnMessageReceived -= ChampSelect_OnMessageReceived;
                        FakePage fakePage = new FakePage();
                        fakePage.Content = Client.LastPageContent;
                        Client.SwitchPage(fakePage);
                        return;
                    }
                    else if (ChampDTO.GameState == "PRE_CHAMP_SELECT")
                    {
                        BanningPhase = true;
                        PurpleBansLabel.Visibility = Visibility.Visible;
                        BlueBansLabel.Visibility = Visibility.Visible;
                        BlueBanListView.Visibility = Visibility.Visible;
                        PurpleBanListView.Visibility = Visibility.Visible;
                        GameStatusLabel.Content = "Bans are on-going";
                        counter = configType.BanTimerDuration - 3;

                        #region Render Bans
                        BlueBanListView.Items.Clear();
                        PurpleBanListView.Items.Clear();
                        foreach (var x in ChampDTO.BannedChampions)
                        {
                            Image champImage = new Image();
                            champImage.Height = 58;
                            champImage.Width = 58;
                            champImage.Source = champions.GetChampion(x.ChampionId).icon;
                            if (x.TeamId == 100)
                            {
                                BlueBanListView.Items.Add(champImage);
                            }
                            else
                            {
                                PurpleBanListView.Items.Add(champImage);
                            }

                            foreach (ListViewItem y in ChampionArray)
                            {
                                if ((int)y.Tag == x.ChampionId)
                                {
                                    ChampionSelectListView.Items.Remove(y);
                                    //Remove from arrays
                                    foreach (ChampionDTO PlayerChamps in ChampList.ToArray())
                                    {
                                        if (x.ChampionId == PlayerChamps.ChampionId)
                                        {
                                            ChampList.Remove(PlayerChamps);
                                            break;
                                        }
                                    }

                                    foreach (ChampionBanInfoDTO BanChamps in ChampionsForBan.ToArray())
                                    {
                                        if (x.ChampionId == BanChamps.ChampionId)
                                        {
                                            ChampionsForBan.Remove(BanChamps);
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        #endregion Render Bans
                    }
                    else if (ChampDTO.GameState == "CHAMP_SELECT")
                    {
                        LastPickTurn = ChampDTO.PickTurn;
                        BanningPhase = false;
                    }
                    else if (ChampDTO.GameState == "POST_CHAMP_SELECT")
                    {
                        HasLockedIn = true;
                        GameStatusLabel.Content = "All players have picked!";
                        if (configType != null)
                            counter = configType.PostPickTimerDuration - 2;
                        else
                            counter = 10;
                    }
                    else if (ChampDTO.GameState == "START_REQUESTED")
                    {
                        GameStatusLabel.Content = "The game is about to start!";
                        DodgeButton.IsEnabled = false; //Cannot dodge past this point!
                        counter = 1;
                    }

                    #region Display players

                    BlueListView.Items.Clear();
                    PurpleListView.Items.Clear();
                    int i = 0;
                    bool PurpleSide = false;

                    //Aram hack, view other players champions & names (thanks to Andrew)
                    List<PlayerChampionSelectionDTO> OtherPlayers = new List<PlayerChampionSelectionDTO>(ChampDTO.PlayerChampionSelections.ToArray());
                    bool AreWePurpleSide = false;

                    foreach (Participant participant in AllParticipants)
                    {
                        Participant tempParticipant = participant;
                        i++;
                        ChampSelectPlayer control = new ChampSelectPlayer();
                        if (tempParticipant is AramPlayerParticipant)
                        {
                            tempParticipant = new PlayerParticipant(tempParticipant.GetBaseTypedObject());
                        }

                        if (tempParticipant is PlayerParticipant)
                        {
                            PlayerParticipant player = tempParticipant as PlayerParticipant;
                            control.PlayerName.Content = player.SummonerName;

                            foreach (PlayerChampionSelectionDTO selection in ChampDTO.PlayerChampionSelections)
                            {
                                #region Disable picking selected champs

                                foreach (ListViewItem y in ChampionArray)
                                {
                                    if ((int)y.Tag == selection.ChampionId)
                                    {
                                        y.IsHitTestVisible = false;
                                        y.Opacity = 0.5;
                                        if (configType != null)
                                        {
                                            if (configType.DuplicatePick)
                                            {
                                                y.IsHitTestVisible = true;
                                                y.Opacity = 1;
                                            }
                                        }
                                    }
                                }

                                #endregion Disable picking selected champs

                                if (selection.SummonerInternalName == player.SummonerInternalName)
                                {
                                    OtherPlayers.Remove(selection);
                                    control = RenderPlayer(selection, player);
                                    if (HasLockedIn && selection.SummonerInternalName == Client.LoginPacket.AllSummonerData.Summoner.InternalName)
                                    {
                                        if (PurpleSide)
                                            AreWePurpleSide = true;
                                        RenderLockInGrid(selection);
                                    }
                                }
                            }
                        }
                        else if (tempParticipant is ObfuscatedParticipant)
                        {
                            control.PlayerName.Content = "Summoner " + i;
                        }
                        else if (tempParticipant is BotParticipant)
                        {
                            BotParticipant bot = tempParticipant as BotParticipant;
                            string botChamp = bot.SummonerName.Split(' ')[0]; //Why is this internal name rito?
                            champions botSelectedChamp = champions.GetChampion(botChamp);
                            PlayerParticipant part = new PlayerParticipant();
                            PlayerChampionSelectionDTO selection = new PlayerChampionSelectionDTO();
                            selection.ChampionId = botSelectedChamp.id;
                            part.SummonerName = botSelectedChamp.displayName + " bot";
                            control = RenderPlayer(selection, part);
                        }
                        else
                        {
                            control.PlayerName.Content = "Unknown Summoner";
                        }
                        if (i > ChampDTO.TeamOne.Count)
                        {
                            i = 0;
                            PurpleSide = true;
                        }

                        if (!PurpleSide)
                        {
                            BlueListView.Items.Add(control);
                        }
                        else
                        {
                            PurpleListView.Items.Add(control);
                        }
                    }

                    //Do aram hack!
                    if (OtherPlayers.Count > 0)
                    {
                        if (AreWePurpleSide)
                        {
                            BlueListView.Items.Clear();
                        }
                        else
                        {
                            PurpleListView.Items.Clear();
                        }

                        foreach (PlayerChampionSelectionDTO hackSelection in OtherPlayers)
                        {
                            ChampSelectPlayer control = new ChampSelectPlayer();
                            PlayerParticipant player = new PlayerParticipant();
                            player.SummonerName = hackSelection.SummonerInternalName;
                            control = RenderPlayer(hackSelection, player);

                            if (AreWePurpleSide)
                            {
                                BlueListView.Items.Add(control);
                            }
                            else
                            {
                                PurpleListView.Items.Add(control);
                            }
                        }
                    }

                    #endregion Display players
                }));

                #endregion In Champion Select
            }
            else if (message.GetType() == typeof(PlayerCredentialsDto))
            {
                #region Launching Game

                PlayerCredentialsDto dto = message as PlayerCredentialsDto;
                Client.CurrentGame = dto;
                Client.PVPNet.OnMessageReceived -= ChampSelect_OnMessageReceived;

                if (!HasLaunchedGame)
                {
                    HasLaunchedGame = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new ThreadStart(() =>
                    {
                        if (CountdownTimer != null)
                        {
                            CountdownTimer.Stop();
                        }
                        Client.PVPNet.OnMessageReceived -= ChampSelect_OnMessageReceived;
                        Client.QuitCurrentGame();
                    }));
                    Client.LaunchGame();
                }

                #endregion Launching Game
            }
        }

        internal void RenderLockInGrid(PlayerChampionSelectionDTO selection)
        {
            ChampionSelectListView.Visibility = Visibility.Hidden;
            AfterChampionSelectGrid.Visibility = Visibility.Visible;

            LockInButton.Content = "Locked In";

            champions Champion = champions.GetChampion(selection.ChampionId);

            SkinSelectListView.Items.Clear();
            AbilityListView.Items.Clear();

            //Render default skin
            ListViewItem item = new ListViewItem();
            Image skinImage = new Image();
            string uriSource = Path.Combine(Client.ExecutingDirectory, "Assets", "champions", Champion.portraitPath);
            skinImage.Source = Client.GetImage(uriSource);
            skinImage.Width = 191;
            skinImage.Stretch = Stretch.UniformToFill;
            item.Tag = "0:" + Champion.id; //Hack
            item.Content = skinImage;
            SkinSelectListView.Items.Add(item);

            List<championAbilities> Abilities = championAbilities.GetAbilities(selection.ChampionId);
            foreach (championAbilities ability in Abilities)
            {
                ChampionAbility championAbility = new ChampionAbility();
                uriSource = Path.Combine(Client.ExecutingDirectory, "Assets", "abilities", ability.iconPath);
                championAbility.AbilityImage.Source = Client.GetImage(uriSource);
                championAbility.AbilityHotKey.Content = ability.hotkey;
                championAbility.AbilityName.Content = ability.name;
                championAbility.AbilityDescription.Text = ability.description;
                championAbility.Width = 375;
                championAbility.Height = 75;
                AbilityListView.Items.Add(championAbility);
            }

            foreach (ChampionDTO champ in ChampList)
            {
                if (champ.ChampionId == selection.ChampionId)
                {
                    foreach (ChampionSkinDTO skin in champ.ChampionSkins)
                    {
                        if (skin.Owned)
                        {
                            item = new ListViewItem();
                            skinImage = new Image();
                            uriSource = Path.Combine(Client.ExecutingDirectory, "Assets", "champions", championSkins.GetSkin(skin.SkinId).portraitPath);
                            skinImage.Source = Client.GetImage(uriSource);
                            skinImage.Width = 191;
                            skinImage.Stretch = Stretch.UniformToFill;
                            item.Tag = skin.SkinId;
                            item.Content = skinImage;
                            SkinSelectListView.Items.Add(item);
                        }
                    }
                }
            }
        }

        internal void RenderChamps(bool RenderBans)
        {
            ChampionSelectListView.Items.Clear();
            if (!RenderBans)
            {
                foreach (ChampionDTO champ in ChampList)
                {
                    champions getChamp = champions.GetChampion(champ.ChampionId);
                    if ((champ.Owned || champ.FreeToPlay) && getChamp.displayName.ToLower().Contains(SearchTextBox.Text.ToLower()))
                    {
                        //Add to ListView
                        ListViewItem item = new ListViewItem();
                        ChampionImage championImage = new ChampionImage();
                        championImage.ChampImage.Source = champions.GetChampion(champ.ChampionId).icon;
                        if (champ.FreeToPlay)
                            championImage.FreeToPlayLabel.Visibility = Visibility.Visible;
                        championImage.Width = 64;
                        championImage.Height = 64;
                        item.Tag = champ.ChampionId;
                        item.Content = championImage.Content;
                        ChampionSelectListView.Items.Add(item);
                    }
                }
            }
            else
            {
                foreach (ChampionBanInfoDTO champ in ChampionsForBan)
                {
                    champions getChamp = champions.GetChampion(champ.ChampionId);
                    if (champ.EnemyOwned && getChamp.displayName.ToLower().Contains(SearchTextBox.Text.ToLower()))
                    {
                        //Add to ListView
                        ListViewItem item = new ListViewItem();
                        ChampionImage championImage = new ChampionImage();
                        championImage.ChampImage.Source = champions.GetChampion(champ.ChampionId).icon;
                        championImage.Width = 64;
                        championImage.Height = 64;
                        item.Tag = champ.ChampionId;
                        item.Content = championImage.Content;
                        ChampionSelectListView.Items.Add(item);
                    }
                }
            }
        }

        internal ChampSelectPlayer RenderPlayer(PlayerChampionSelectionDTO selection, PlayerParticipant player)
        {
            ChampSelectPlayer control = new ChampSelectPlayer();
            if (selection.ChampionId != 0)
            {
                control.ChampionImage.Source = champions.GetChampion(selection.ChampionId).icon;
            }
            if (selection.Spell1Id != 0)
            {
                string uriSource = Path.Combine(Client.ExecutingDirectory, "Assets", "spell", SummonerSpell.GetSpellImageName((int)selection.Spell1Id));
                control.SummonerSpell1.Source = Client.GetImage(uriSource);
                uriSource = Path.Combine(Client.ExecutingDirectory, "Assets", "spell", SummonerSpell.GetSpellImageName((int)selection.Spell2Id));
                control.SummonerSpell2.Source = Client.GetImage(uriSource);
            }
            if (player.SummonerName == Client.LoginPacket.AllSummonerData.Summoner.Name)
            {
                string uriSource = Path.Combine(Client.ExecutingDirectory, "Assets", "spell", SummonerSpell.GetSpellImageName((int)selection.Spell1Id));
                SummonerSpell1Image.Source = Client.GetImage(uriSource);
                uriSource = Path.Combine(Client.ExecutingDirectory, "Assets", "spell", SummonerSpell.GetSpellImageName((int)selection.Spell2Id));
                SummonerSpell2Image.Source = Client.GetImage(uriSource);
            }
            control.PlayerName.Content = player.SummonerName;
            return control;
        }

        private async void ListViewItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var item = sender as ListViewItem;
            if (item != null)
            {
                if (!BanningPhase)
                {
                    if (item.Tag != null)
                    {
                        await Client.PVPNet.SelectChampion((int)item.Tag);

                        //TODO: Fix stupid animation glitch on left hand side
                        DoubleAnimation fadingAnimation = new DoubleAnimation();
                        fadingAnimation.From = 0.4;
                        fadingAnimation.To = 0;
                        fadingAnimation.Duration = new Duration(TimeSpan.FromSeconds(0.2));
                        fadingAnimation.Completed += (eSender, eArgs) =>
                        {
                            string uriSource = Path.Combine(Client.ExecutingDirectory, "Assets", "champions", champions.GetChampion((int)item.Tag).splashPath);
                            BackgroundSplash.Source = Client.GetImage(uriSource);
                            fadingAnimation = new DoubleAnimation();
                            fadingAnimation.From = 0;
                            fadingAnimation.To = 0.4;
                            fadingAnimation.Duration = new Duration(TimeSpan.FromSeconds(0.5));

                            BackgroundSplash.BeginAnimation(Image.OpacityProperty, fadingAnimation);
                        };

                        BackgroundSplash.BeginAnimation(Image.OpacityProperty, fadingAnimation);
                    }
                }
                else
                {
                    if (item.Tag != null)
                    {
                        await Client.PVPNet.BanChampion((int)item.Tag);
                    }
                }
            }
        }

        private async void SkinSelectListView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var item = sender as ListViewItem;
            if (item != null)
            {
                if (item.Tag != null)
                {
                    if (item.Tag is string)
                    {
                        string[] splitItem = ((string)item.Tag).Split(':');
                        int championId = Convert.ToInt32(splitItem[1]);
                        champions Champion = champions.GetChampion(championId);
                        await Client.PVPNet.SelectChampionSkin(championId, 0);
                        TextRange tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                        tr.Text = "Selected Default " + Champion.name + " as skin" + Environment.NewLine;
                        tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.White);
                    }
                    else
                    {
                        championSkins skin = championSkins.GetSkin((int)item.Tag);
                        await Client.PVPNet.SelectChampionSkin(skin.championId, skin.id);
                        TextRange tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                        tr.Text = "Selected " + skin.displayName + " as skin" + Environment.NewLine;
                        tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.White);
                    }
                }
            }
        }

        private void DodgeButton_Click(object sender, RoutedEventArgs e)
        {
            //TODO - add messagebox
            Client.PVPNet.OnMessageReceived -= ChampSelect_OnMessageReceived;
            Client.QuitCurrentGame();
        }

        private async void LockInButton_Click(object sender, RoutedEventArgs e)
        {
            if (ChampionSelectListView.SelectedItems.Count > 0)
            {
                await Client.PVPNet.ChampionSelectCompleted();
                HasLockedIn = true;
            }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RenderChamps(BanningPhase);
        }

        private void EditMasteriesButton_Click(object sender, RoutedEventArgs e)
        {
            Client.OverlayContainer.Content = new MasteriesOverlay().Content;
            Client.OverlayContainer.Visibility = Visibility.Visible;
        }

        private void EditRunesButton_Click(object sender, RoutedEventArgs e)
        {
            Client.OverlayContainer.Content = new RunesOverlay().Content;
            Client.OverlayContainer.Visibility = Visibility.Visible;
        }

        private void SummonerSpell_Click(object sender, RoutedEventArgs e)
        {
            Client.OverlayContainer.Content = new SelectSummonerSpells(LatestDto.GameMode).Content;
            Client.OverlayContainer.Visibility = Visibility.Visible;
        }

        private async void MasteryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!QuickLoad) //Make loading quicker
                return;

            bool HasChanged = false;
            int i = 0;
            MasteryBookDTO bookDTO = new MasteryBookDTO();
            bookDTO.SummonerId = Client.LoginPacket.AllSummonerData.Summoner.SumId;
            bookDTO.BookPages = new List<MasteryBookPageDTO>();
            foreach (MasteryBookPageDTO MasteryPage in MyMasteries.BookPages)
            {
                string MasteryPageName = MasteryPage.Name;
                if (MasteryPageName.StartsWith("@@"))
                {
                    MasteryPageName = "Mastery Page " + ++i;
                }
                MasteryPage.Current = false;
                if (MasteryPageName == (string)MasteryComboBox.SelectedItem)
                {
                    MasteryPage.Current = true;
                    HasChanged = true;
                    TextRange tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                    tr.Text = "Selected " + MasteryPageName + " as Mastery Page" + Environment.NewLine;
                    tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.White);
                }
                bookDTO.BookPages.Add(MasteryPage);
            }
            if (HasChanged)
            {
                await Client.PVPNet.SaveMasteryBook(bookDTO);
            }
        }

        private async void RuneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!QuickLoad) //Make loading quicker
                return;

            SpellBookPageDTO SelectedRunePage = new SpellBookPageDTO();
            int i = 0;
            bool HasChanged = false;
            foreach (SpellBookPageDTO RunePage in MyRunes.BookPages)
            {
                string RunePageName = RunePage.Name;
                if (RunePageName.StartsWith("@@"))
                {
                    RunePageName = "Rune Page " + ++i;
                }
                RunePage.Current = false;
                if (RunePageName == (string)RuneComboBox.SelectedItem)
                {
                    RunePage.Current = true;
                    SelectedRunePage = RunePage;
                    HasChanged = true;
                    TextRange tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                    tr.Text = "Selected " + RunePageName + " as Rune Page" + Environment.NewLine;
                    tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.White);
                }
            }
            if (HasChanged)
            {
                await Client.PVPNet.SelectDefaultSpellBookPage(SelectedRunePage);
            }
        }

        private void ChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (ChatTextBox.Text == "!~dev")
            {
                DevMode = !DevMode;
                ChampionSelectListView.IsHitTestVisible = true;
                ChampionSelectListView.Opacity = 1;
                TextRange tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                tr.Text = "DEV MODE: " + DevMode;
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Yellow);
            }
            else
            {
                if (DevMode)
                {
                    if (ChatTextBox.Text == "!~champ")
                    {
                        ChampionSelectListView.Visibility = Visibility.Visible;
                        AfterChampionSelectGrid.Visibility = Visibility.Hidden;
                    }
                    else if (ChatTextBox.Text == "!~skin")
                    {
                        ChampionSelectListView.Visibility = Visibility.Hidden;
                        AfterChampionSelectGrid.Visibility = Visibility.Visible;
                    }
                    return;
                }
                TextRange tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                tr.Text = Client.LoginPacket.AllSummonerData.Summoner.Name + ": ";
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Yellow);
                tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                tr.Text = ChatTextBox.Text + Environment.NewLine;
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.White);
                Chatroom.PublicMessage(ChatTextBox.Text);
                ChatTextBox.Text = "";
            }
        }

        private void Chatroom_OnRoomMessage(object sender, jabber.protocol.client.Message msg)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new ThreadStart(() =>
            {
                if (msg.Body != "This room is not anonymous")
                {
                    TextRange tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                    tr.Text = msg.From.Resource + ": ";
                    tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Blue);
                    tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                    tr.Text = msg.InnerText.Replace("<![CDATA[", "").Replace("]]>", "") + Environment.NewLine;
                    tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.White);
                }
            }));
        }

        private void Chatroom_OnParticipantJoin(Room room, RoomParticipant participant)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new ThreadStart(() =>
            {
                TextRange tr = new TextRange(ChatText.Document.ContentEnd, ChatText.Document.ContentEnd);
                tr.Text = participant.Nick + " joined the room." + Environment.NewLine;
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Yellow);
            }));
        }
    }
}