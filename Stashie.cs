using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using SharpDX;
using Vector4 = System.Numerics.Vector4;
// ReSharper disable StringLiteralTypo

namespace Stashie
{
    public class StashieCore : BaseSettingsPlugin<StashieSettings>
    {
        private const string StashTabsNameChecker = "Stash Tabs Name Checker";
        private const string FiltersConfigFilePrimary = "FiltersConfig.txt";
        private const int WhileDelay = 5;
        private const int InputDelay = 15;
        private const string CoroutineName = "Drop To Stash";
        private readonly Stopwatch _debugTimer = new Stopwatch();
        private readonly Stopwatch _stackItemTimer = new Stopwatch();
        private readonly WaitTime _wait10Ms = new WaitTime(10);
        private readonly WaitTime _wait3Ms = new WaitTime(3);
        private Vector2 _clickWindowOffset;
        private List<CustomFilter> _customFiltersPrimary;
        private List<RefillProcessor> _customRefills;
        private List<FilterResult> _dropItems;
        private List<ListIndexNode> _settingsListNodes;
        private uint _coroutineIteration;
        private Coroutine _coroutineWorker;
        private Action _filterTabs;
        private string[] _stashTabNamesByIndex;
        private Coroutine _stashTabNamesCoroutine;
        private int _visibleStashIndex = -1;
        private const int MaxShownSidebarStashTabs = 31;
        private int _stashCount;
        private NormalInventoryItem lastHoverItem;

        public StashieCore()
        {
            Name = "Stashie";
        }

        public override void ReceiveEvent(string eventId, object args)
        {
            if (!Settings.Enable.Value) return;

            switch (eventId)
            {
                case "switch_to_tab":
                    HandleSwitchToTabEvent(args);
                    break;
                default:
                    break;
            }
        }

        private void HandleSwitchToTabEvent(object tab)
        {
            switch (tab)
            {
                case int index:
                    _coroutineWorker = new Coroutine(ProcessSwitchToTab(index), this, CoroutineName);
                    break;
                case string name:
                    if (!_renamedAllStashNames.Contains(name))
                    {
                        DebugWindow.LogMsg($"{Name}: can't find tab with name '{name}'.");
                        break;
                    }

                    var tempIndex = _renamedAllStashNames.IndexOf(name);
                    _coroutineWorker = new Coroutine(ProcessSwitchToTab(tempIndex), this, CoroutineName);
                    DebugWindow.LogMsg($"{Name}: Switching to tab with index: {tempIndex} ('{name}').");
                    break;
                default:
                    DebugWindow.LogMsg("The received argument is not a string or an integer.");
                    break;
            }

            Core.ParallelRunner.Run(_coroutineWorker);
        }

        public override bool Initialise()
        {
            Settings.Enable.OnValueChanged += (sender, b) =>
            {
                if (b)
                {
                    if (Core.ParallelRunner.FindByName(StashTabsNameChecker) == null) InitStashTabNameCoRoutine();
                    _stashTabNamesCoroutine?.Resume();
                }
                else
                {
                    _stashTabNamesCoroutine?.Pause();
                }

                SetupOrClose();
            };

            InitStashTabNameCoRoutine();
            SetupOrClose();

            Input.RegisterKey(Settings.DropHotkey);

            Settings.DropHotkey.OnValueChanged += () => { Input.RegisterKey(Settings.DropHotkey); };
            _stashCount = (int) GameController.Game.IngameState.IngameUi.StashElement.TotalStashes;

            return true;
        }

        public override void AreaChange(AreaInstance area)
        {
            if (_stashTabNamesCoroutine == null) return;
            if (_stashTabNamesCoroutine.Running)
            {
                if (!area.IsHideout && !area.IsTown &&
                    !area.DisplayName.Contains("Azurite Mine") &&
                    !area.DisplayName.Contains("Tane's Laboratory"))
                    _stashTabNamesCoroutine?.Pause();
            }
            else
            {
                if (area.IsHideout ||
                    area.IsTown ||
                    area.DisplayName.Contains("Azurite Mine") ||
                    area.DisplayName.Contains("Tane's Laboratory"))
                    _stashTabNamesCoroutine?.Resume();
            }
        }

        private void InitStashTabNameCoRoutine()
        {
            _stashTabNamesCoroutine = new Coroutine(StashTabNamesUpdater_Thread(), this, StashTabsNameChecker);
            Core.ParallelRunner.Run(_stashTabNamesCoroutine);
        }

        /// <summary>
        /// Creates a new file and adds the content to it if the file doesn't exists.
        /// If the file already exists, then no action is taken.
        /// </summary>
        /// <param name="path">The path to the file on disk</param>
        /// <param name="content">The content it should contain</param>
        private static void WriteToNonExistentFile(string path, string content)
        {
            if (File.Exists(path)) return;

            using (var streamWriter = new StreamWriter(path, true))
            {
                streamWriter.Write(content);
                streamWriter.Close();
            }
        }

        private void SaveDefaultConfigsToDisk()
        {
            var path = $"{DirectoryFullName}\\GitUpdateConfig.txt";
            const string gitUpdateConfig = "Owner:nymann\r\n" + "Name:Stashie\r\n" + "Release\r\n";
            WriteToNonExistentFile(path, gitUpdateConfig);
            path = $"{DirectoryFullName}\\RefillCurrency.txt";

            const string refillCurrency = "//MenuName:\t\t\tClassName,\t\t\tStackSize,\tInventoryX,\tInventoryY\r\n" +
                                          "Portal Scrolls:\t\tPortal Scroll,\t\t40,\t\t\t12,\t\t\t1\r\n" +
                                          "Scrolls of Wisdom:\tScroll of Wisdom,\t40,\t\t\t12,\t\t\t2\r\n" +
                                          "//Chances:\t\t\tOrb of Chance,\t\t20,\t\t\t12,\t\t\t3";

            WriteToNonExistentFile(path, refillCurrency);
            path = $"{DirectoryFullName}\\FiltersConfig.txt";

            const string filtersConfig =

                #region default config String

                @"
//FilterName(menu name):	filters		:ParentMenu(optionaly, will be created automatially for grouping)
//Filter parts should divided by coma or | (for OR operation(any filter part can pass))

////////////	Available properties:	/////////////////////
/////////	String (name) properties:
//classname
//basename
//path
/////////	Numerical properties:
//itemquality
//rarity
//ilvl
//tier
//numberofsockets
//numberoflinks
//veiled
//fractured
/////////	Boolean properties:
//identified
//fractured
//corrupted
//influenced
//Elder
//Shaper
//Crusader
//Hunter
//Redeemer
//Warlord
//blightedMap
//elderGuardianMap
/////////////////////////////////////////////////////////////
////////////	Available operations:	/////////////////////
/////////	String (name) operations:
//!=	(not equal)
//=		(equal)
//^		(contains)
//!^	(not contains)
/////////	Numerical operations:
//!=	(not equal)
//=		(equal)
//>		(bigger)
//<		(less)
//<=	(less or qual)
//>=	(bigger or qual)
/////////	Boolean operations:
//!		(not/invert)
/////////////////////////////////////////////////////////////

// Optimized for 200 pts during sale
// Buy **first blood** $20 pack on sale (200 points), currency, fragment, esssense and upgrade 2 tabs to premium, buy 1 premium 
// P = Premium, N = normal, S = special, (C) - extra slot in currency tab, (O) - any other tab
// 0 - some stuff for human play (maps metamorph etc)
// 1 premium - Priority loot
// 2 normal - Blight Maps + Incubators
// 3 normal - Quality gems (future bot automation)
// 4 normal - Divination cards (future bot automation)
// 5 premium - Delve + Prophecies
// 6 special fragments
// 7 special essences
// 8 special currency
// 9 premium Dump - everything else

// 0P_Other
Unique Rings:		Rarity=Unique,ClassName=Ring									:0P_Other
Metamorph organs:	ClassName=MetamorphosisDNA										:0P_Other
Catalysts:			BaseName^Catalyst												:0P_Other
ID Jewelery: 		ClassName=Amulet|ClassName=Ring,identified 						:0P_Other
ID Jewels:	 		ClassName=AbyssJewel|ClassName=Jewel,identified			 		:0P_Other
Heist Contract: 	BaseName^Contract 												:0P_Other
Heist Blueprint: 	BaseName^Blueprint												:0P_Other

// 1P_PriorityLoot (Sell from here)
Top prophecies: 	BaseName^Trash to Treasure|BaseName^The Queen's Sacrifice|BaseName^Fated Connections|BaseName^Fire and Brimstone|BaseName^A Dishonourable Death|BaseName^Darktongue's Shriek|BaseName^Monstrous Treasure|BaseName^Song of the Sekhema|BaseName^Ending the Torment|BaseName^The Bowstring's Music 		:1P_PriorityLoot
Top div cards: 		BaseName^House of Mirrors|BaseName^The Demon|BaseName^The Immortal|BaseName^The Doctor|BaseName^The Cheater|BaseName^The Fiend|BaseName^Beauty Through Death|BaseName^Alluring Bounty|BaseName^The Iron Bard|BaseName^Seven Years Bad Luck|BaseName^Succor of the Sinless|BaseName^The Damned|BaseName^The Samurai's Eye|BaseName^Abandoned Wealth|BaseName^The Nurse|BaseName^Gift of Asenath|BaseName^Nook's Crown|BaseName^Immortal Resolve|BaseName^Wealth and Power|BaseName^The Sustenance|BaseName^The Awakened|BaseName^Pride of the First Ones|BaseName^The Price of Loyalty|BaseName^Prometheus' Armoury|BaseName^A Familiar Call|BaseName^The Dragon's Heart|BaseName^The Greatest Intentions|BaseName^The Craving|BaseName^The Saint's Treasure|BaseName^The Mayor|BaseName^The White Knight|BaseName^The Long Con|BaseName^The Celestial Stone|BaseName^The Escape|BaseName^Void of the Elements|BaseName^Squandered Prosperity|BaseName^The Hive of Knowledge|BaseName^Dark Dreams|BaseName^The Lord of Celebration|BaseName^Echoes of Love 		:1P_PriorityLoot
All L21Q23 gems: 	ItemQuality>20,skillgemlevel>20 								:1P_PriorityLoot
Good L21 gems: 		BaseName^Vaal Discipline,skillgemlevel>20|BaseName^Hatred,skillgemlevel>20|BaseName^Precision,skillgemlevel>20|BaseName^Wrath,skillgemlevel>20|BaseName^Purity of Fire,skillgemlevel>20|BaseName^Blood Magic,skillgemlevel>20|BaseName^Anger,skillgemlevel>20|BaseName^Vitality,skillgemlevel>20|BaseName^Animate Guardian,skillgemlevel>20|BaseName^Purity of Ice,skillgemlevel>20|BaseName^Discipline,skillgemlevel>20|BaseName^Vaal Righteous Fire,skillgemlevel>20|BaseName^Zealotry,skillgemlevel>20|BaseName^Raise Spectre,skillgemlevel>20|BaseName^Vaal Haste,skillgemlevel>20|BaseName^Herald of Purity,skillgemlevel>20|BaseName^Hypothermia Support,skillgemlevel>20|BaseName^Cast when Damage Taken,skillgemlevel>20|BaseName^Hexblast,skillgemlevel>20|BaseName^Inspiration,skillgemlevel>20|BaseName^Ice Nova,skillgemlevel>20|BaseName^Vaal Grace,skillgemlevel>20												:1P_PriorityLoot
Good Q23 gems: 		ClassName^Skill Gem,BaseName^Temporal Chains,ItemQuality>20|ClassName^Skill Gem,BaseName^Kinetic Bolt,ItemQuality>20|ClassName^Skill Gem,BaseName^Sniper's Mark,ItemQuality>20|ClassName^Skill Gem,BaseName^Galvanic Arrow,ItemQuality>20|ClassName^Skill Gem,BaseName^Hydrosphere,ItemQuality>20|ClassName^Skill Gem,BaseName^Power Charge On Critical Support,ItemQuality>20|ClassName^Skill Gem,BaseName^High-Impact Mine,ItemQuality>20|ClassName^Skill Gem,BaseName^Swift Assembly,ItemQuality>20|ClassName^Skill Gem,BaseName^Lightning Arrow,ItemQuality>20|ClassName^Skill Gem,BaseName^Barrage,ItemQuality>20|ClassName^Skill Gem,BaseName^Vaal Haste,ItemQuality>20|ClassName^Skill Gem,BaseName^Block Chance Reduction,ItemQuality>20|ClassName^Skill Gem,BaseName^Vaal Righteous Fire,ItemQuality>20|ClassName^Skill Gem,BaseName^Bonechill,ItemQuality>20|ClassName^Skill Gem,BaseName^Physical to Lightning,ItemQuality>20|ClassName^Skill Gem,BaseName^Slower Projectiles,ItemQuality>20|ClassName^Skill Gem,BaseName^Berserk,ItemQuality>20|ClassName^Skill Gem,BaseName^Vaal Discipline,ItemQuality>20|ClassName^Skill Gem,BaseName^Vaal Grace,ItemQuality>20|ClassName^Skill Gem,BaseName^Vaal Molten Shell,ItemQuality>20|ClassName^Skill Gem,BaseName^Increased Duration,ItemQuality>20|ClassName^Skill Gem,BaseName^Pinpoint,ItemQuality>20|ClassName^Skill Gem,BaseName^Minefield,ItemQuality>20|ClassName^Skill Gem,BaseName^Less Duration,ItemQuality>20|ClassName^Skill Gem,BaseName^Hextouch,ItemQuality>20|ClassName^Skill Gem,BaseName^Greater Multiple Projectiles,ItemQuality>20|ClassName^Skill Gem,BaseName^Cast when Damage Taken,ItemQuality>20|ClassName^Skill Gem,BaseName^Greater Volley,ItemQuality>20|ClassName^Skill Gem,BaseName^Inspiration,ItemQuality>20|ClassName^Skill Gem,BaseName^Blood Magic,ItemQuality>20|ClassName^Skill Gem,BaseName^Vaal Ancestral Warchief,ItemQuality>20|ClassName^Skill Gem,BaseName^Vitality,ItemQuality>20|ClassName^Skill Gem,BaseName^Determination,ItemQuality>20 			:1P_PriorityLoot
All Q23 gems: 		ItemQuality>20 													:1P_PriorityLoot
All L21 gems: 		skillgemlevel>20 												:1P_PriorityLoot
Other top: 			BaseName^Golden Oil|BaseName^Silver Oil|BaseName^Opalescent Oil|BaseName^Delirium|path^CurrencyAfflictionShard|path^CurrencyAfflictionFragment|BaseName^Enriched|BaseName^Empower|BaseName^Enlighten 		:1P_PriorityLoot

// 2N_BlightMaps_Incubators
Blighted Maps: 		ClassName=Map,blightedMap,!elderGuardianMap 					:2N_BlightMaps_Incubators
Incubators:			ClassName^Incubator												:2N_BlightMaps_Incubators
Other Maps: 		ClassName=Map 													:2N_BlightMaps_Incubators

// 3N_Quality
Quality Gems:		ClassName^Skill Gem,ItemQuality>0								:3N_Quality
Quality Flasks:		ClassName^Flask,ItemQuality>0									:3N_Quality

// 4N_Divination
Divination Cards:	ClassName=DivinationCard										:4N_Divination

// 5P_Delve_Prophecies
Breachstone: 		BaseName^Breachstone				 							:5P_Delve_Prophecies
Prophecies:			ClassName!^QuestItem,BaseName=Prophecy							:5P_Delve_Prophecies
Fossils: 			BaseName^Fossil													:5P_Delve_Prophecies
Resonator 4:        BaseName^Prime,ClassName=DelveStackableSocketableCurrency       :5P_Delve_Prophecies
Resonator 3:        BaseName^Powerful,ClassName=DelveStackableSocketableCurrency 	:5P_Delve_Prophecies
Resonator 2:        BaseName^Potent,ClassName=DelveStackableSocketableCurrency 		:5P_Delve_Prophecies
Resonator 1:        BaseName^Primitiv,ClassName=DelveStackableSocketableCurrency 	:5P_Delve_Prophecies

// 6S_Frags
Breach Splinter: 	BaseName^Splinter,Path^CurrencyBreach 							:6S_Frags
TimeLess Splinter: 	BaseName^Splinter,Path^CurrencyLegion 							:6S_Frags
Scarab:				BaseName^Scarab													:6S_Frags
Sacrifice:			BaseName^Sacrifice												:6S_Frags
Map Fragments: 		ClassName=MapFragment 											:6S_Frags
Offerings: 			ClassName=LabyrinthMapItem 										:6S_Frags

// 7S_Essences
Essences:			BaseName^Essence,ClassName=StackableCurrency					:7S_Essences

// 8S_Currency
Oils(C): 			BaseName^Oil,Path^Mushrune 										:8S_Currency
Stacked Decks (C):	ClassName=StackableCurrency,BaseName=Stacked Deck 				:8S_Currency
Currency:			ClassName=StackableCurrency 									:8S_Currency

// 9P_Dump
Veiled:				veiled > 0														:9P_Dump
Rare ID: 			Rarity=Rare,identified 											:9P_Dump
Influenced: 		influenced 														:9P_Dump
Everything else:	Rarity=Normal|Rarity=Magic|Rarity=Unique|!identified 			:9P_Dump

";

            #endregion

            WriteToNonExistentFile(path, filtersConfig);
        }

        public override void DrawSettings()
        {
            DrawReloadConfigButton();
            DrawIgnoredCellsSettings();
            base.DrawSettings();

            foreach (var settingsCustomRefillOption in Settings.CustomRefillOptions)
            {
                var value = settingsCustomRefillOption.Value.Value;
                ImGui.SliderInt(settingsCustomRefillOption.Key, ref value, settingsCustomRefillOption.Value.Min,
                    settingsCustomRefillOption.Value.Max);
                settingsCustomRefillOption.Value.Value = value;
            }

            _filterTabs?.Invoke();
        }

        private void LoadCustomFilters()
        {
            var filter = FiltersConfigFilePrimary;

            var filterFilePath = Path.Combine(DirectoryFullName, filter);
            var filterLines = File.ReadAllLines(filterFilePath);
            _customFiltersPrimary = FilterParser.Parse(filterLines);

            foreach (var customFilter in _customFiltersPrimary)
            {
                if (!Settings.CustomFilterOptions.TryGetValue(customFilter.Name, out var indexNodeS))
                {
                    indexNodeS = new ListIndexNode {Value = "Ignore", Index = -1};
                    Settings.CustomFilterOptions.Add(customFilter.Name, indexNodeS);
                }

                customFilter.StashIndexNode = indexNodeS;
                _settingsListNodes.Add(indexNodeS);
            }
        }

        public void SaveIgnoredSLotsFromInventoryTemplate()
        {
            Settings.IgnoredCells = new[,]
            {
                {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
                {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
                {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
                {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
                {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
            };
            try
            {
                var inventory = GameController.Game.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory];
                foreach (var item in inventory.VisibleInventoryItems)
                {
                    var baseC = item.Item.GetComponent<Base>();
                    var itemSizeX = baseC.ItemCellsSizeX;
                    var itemSizeY = baseC.ItemCellsSizeY;
                    var inventPosX = item.InventPosX;
                    var inventPosY = item.InventPosY;
                    for (var y = 0; y < itemSizeY; y++)
                    for (var x = 0; x < itemSizeX; x++)
                        Settings.IgnoredCells[y + inventPosY, x + inventPosX] = 1;
                }
            }
            catch (Exception e)
            {
                LogError($"{e}", 5);
            }
        }

        private void DrawReloadConfigButton()
        {
            if (ImGui.Button("Reload config"))
            {
                LoadCustomFilters();
                GenerateMenu();
                DebugWindow.LogMsg("Reloaded Stashie config", 2, Color.LimeGreen);
            }
        }

        private void DrawIgnoredCellsSettings()
        {
            try
            {
                if (ImGui.Button("Copy Inventory")) SaveIgnoredSLotsFromInventoryTemplate();

                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        $"Checked = Item will be ignored{Environment.NewLine}UnChecked = Item will be processed");
            }
            catch (Exception e)
            {
                LogError(e.ToString(), 10);
            }

            var numb = 1;
            for (var i = 0; i < 5; i++)
            for (var j = 0; j < 12; j++)
            {
                var toggled = Convert.ToBoolean(Settings.IgnoredCells[i, j]);
                if (ImGui.Checkbox($"##{numb}IgnoredCells", ref toggled)) Settings.IgnoredCells[i, j] ^= 1;

                if ((numb - 1) % 12 < 11) ImGui.SameLine();

                numb += 1;
            }
        }

        private void GenerateMenu()
        {
            _stashTabNamesByIndex = _renamedAllStashNames.ToArray();

            _filterTabs = null;

            foreach (var customFilter in _customFiltersPrimary.GroupBy(x => x.SubmenuName, e => e))
                _filterTabs += () =>
                {
                    ImGui.TextColored(new Vector4(0f, 1f, 0.022f, 1f), customFilter.Key);

                    foreach (var filter in customFilter)
                        if (Settings.CustomFilterOptions.TryGetValue(filter.Name, out var indexNode))
                        {
                            var formattableString = $"{filter.Name} => {_renamedAllStashNames[indexNode.Index + 1]}";

                            ImGui.Columns(2, formattableString, true);
                            ImGui.SetColumnWidth(0, 300);
                            ImGui.SetColumnWidth(1, 160);

                            if (ImGui.Button(formattableString, new System.Numerics.Vector2(180, 20)))
                                ImGui.OpenPopup(formattableString);

                            ImGui.SameLine();
                            ImGui.NextColumn();

                            var item = indexNode.Index + 1;
                            var filterName = filter.Name;

                            if (string.IsNullOrWhiteSpace(filterName))
                                filterName = "Null";

                            if (ImGui.Combo($"##{filterName}", ref item, _stashTabNamesByIndex,
                                _stashTabNamesByIndex.Length))
                            {
                                indexNode.Value = _stashTabNamesByIndex[item];
                                OnSettingsStashNameChanged(indexNode, _stashTabNamesByIndex[item]);
                            }

                            ImGui.NextColumn();
                            ImGui.Columns(1, "", false);
                            var pop = true;

                            if (!ImGui.BeginPopupModal(formattableString, ref pop,
                                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize)) continue;
                            var x = 0;

                            foreach (var name in _renamedAllStashNames)
                            {
                                x++;

                                if (ImGui.Button($"{name}", new System.Numerics.Vector2(100, 20)))
                                {
                                    indexNode.Value = name;
                                    OnSettingsStashNameChanged(indexNode, name);
                                    ImGui.CloseCurrentPopup();
                                }

                                if (x % 10 != 0)
                                    ImGui.SameLine();
                            }

                            ImGui.Spacing();
                            ImGuiNative.igIndent(350);
                            if (ImGui.Button("Close", new System.Numerics.Vector2(100, 20)))
                                ImGui.CloseCurrentPopup();

                            ImGui.EndPopup();
                        }
                        else
                        {
                            indexNode = new ListIndexNode {Value = "Ignore", Index = -1};
                        }
                };
        }

        private void LoadCustomRefills()
        {
            _customRefills = RefillParser.Parse(DirectoryFullName);
            if (_customRefills.Count == 0) return;

            foreach (var refill in _customRefills)
            {
                if (!Settings.CustomRefillOptions.TryGetValue(refill.MenuName, out var amountOption))
                {
                    amountOption = new RangeNode<int>(15, 0, refill.StackSize);
                    Settings.CustomRefillOptions.Add(refill.MenuName, amountOption);
                }

                amountOption.Max = refill.StackSize;
                refill.AmountOption = amountOption;
            }

            _settingsListNodes.Add(Settings.CurrencyStashTab);
        }

        public override Job Tick()
        {
            if (!stashingRequirementsMet() && Core.ParallelRunner.FindByName("Stashie_DropItemsToStash") != null)
            {
                StopCoroutine("Stashie_DropItemsToStash");
                return null;
            }

            if (Settings.DropHotkey.PressedOnce())
            {
                if (Core.ParallelRunner.FindByName("Stashie_DropItemsToStash") == null)
                    StartDropItemsToStashCoroutine();
                else
                    StopCoroutine("Stashie_DropItemsToStash");
            }

            return null;
        }

        private void StartDropItemsToStashCoroutine()
        {
            _debugTimer.Reset();
            _debugTimer.Start();
            Core.ParallelRunner.Run(new Coroutine(DropToStashRoutine(), this, "Stashie_DropItemsToStash"));
        }

        private void StopCoroutine(string routineName)
        {
            var routine = Core.ParallelRunner.FindByName(routineName);
            routine?.Done();
            _debugTimer.Stop();
            _debugTimer.Reset();
            CleanUp();
        }

        private IEnumerator DropToStashRoutine()
        {
            var cursorPosPreMoving = Input.ForceMousePosition; //saving cursorposition
            //try stashing items 3 times
            var originTab = GetIndexOfCurrentVisibleTab();
            yield return ParseItems();
            for (var tries = 0; tries < 3 && _dropItems.Count > 0; ++tries)
            {
                if (_dropItems.Count > 0)
                    yield return StashItemsIncrementer();
                yield return ParseItems();
                yield return new WaitTime(Settings.ExtraDelay);
            }

            //yield return ProcessRefills(); currently bugged
            if (Settings.VisitTabWhenDone.Value)
            {
                if (Settings.BackToOriginalTab.Value)
                    yield return SwitchToTab(originTab);
                else
                    yield return SwitchToTab(Settings.TabToVisitWhenDone.Value);
            }


            //restoring cursorposition
            Input.SetCursorPos(cursorPosPreMoving);
            Input.MouseMove();
            StopCoroutine("Stashie_DropItemsToStash");
        }

        private void CleanUp()
        {
            Input.KeyUp(Keys.LControlKey);
            Input.KeyUp(Keys.Shift);
        }

        private bool stashingRequirementsMet()
        {
            return GameController.Game.IngameState.IngameUi.InventoryPanel.IsVisible &&
                   GameController.Game.IngameState.IngameUi.StashElement.IsVisibleLocal;
        }

        private IEnumerator ProcessInventoryItems()
        {
            _debugTimer.Restart();
            yield return ParseItems();

            var cursorPosPreMoving = Input.ForceMousePosition;
            if (_dropItems.Count > 0)
                yield return StashItemsIncrementer();

            yield return ProcessRefills();
            yield return Input.SetCursorPositionSmooth(new Vector2(cursorPosPreMoving.X, cursorPosPreMoving.Y));
            Input.MouseMove();

            _coroutineWorker = Core.ParallelRunner.FindByName(CoroutineName);
            _coroutineWorker?.Done();

            _debugTimer.Restart();
            _debugTimer.Stop();
        }

        private IEnumerator ProcessSwitchToTab(int index)
        {
            _debugTimer.Restart();
            yield return SwitchToTab(index);
            _coroutineWorker = Core.ParallelRunner.FindByName(CoroutineName);
            _coroutineWorker?.Done();

            _debugTimer.Restart();
            _debugTimer.Stop();
        }

        private IEnumerator ParseItems()
        {
            var inventory = GameController.Game.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory];
            var invItems = inventory.VisibleInventoryItems;

            yield return new WaitFunctionTimed(() => invItems != null, true, 500,
                "Player inventory->VisibleInventoryItems is null!");
            _dropItems = new List<FilterResult>();
            _clickWindowOffset = GameController.Window.GetWindowRectangle().TopLeft;
            foreach (var invItem in invItems)
            {
                if (invItem.Item == null || invItem.Address == 0) continue;
                if (CheckIgnoreCells(invItem)) continue;
                var baseItemType = GameController.Files.BaseItemTypes.Translate(invItem.Item.Path);
                var testItem = new ItemData(invItem, baseItemType);
                //LogMessage(testItem.ToString());
                var result = CheckFilters(testItem);
                if (result != null)
                    _dropItems.Add(result);
            }
        }

        private bool CheckIgnoreCells(NormalInventoryItem inventItem)
        {
            var inventPosX = inventItem.InventPosX;
            var inventPosY = inventItem.InventPosY;

            if (Settings.RefillCurrency &&
                _customRefills.Any(x => x.InventPos.X == inventPosX && x.InventPos.Y == inventPosY))
                return true;

            if (inventPosX < 0 || inventPosX >= 12) return true;

            if (inventPosY < 0 || inventPosY >= 5) return true;

            return Settings.IgnoredCells[inventPosY, inventPosX] != 0; //No need to check all item size
        }

        private FilterResult CheckFilters(ItemData itemData)
        {
            foreach (var filter in _customFiltersPrimary)
                try
                {
                    if (!filter.AllowProcess) continue;

                    if (filter.CompareItem(itemData)) return new FilterResult(filter, itemData);
                }
                catch (Exception ex)
                {
                    DebugWindow.LogError($"Check filters error: {ex}");
                }

            return null;
        }

        private IEnumerator StashItemsIncrementer()
        {
            _coroutineIteration++;

            yield return StashItems();
        }

        private IEnumerator StashItems()
        {
            PublishEvent("stashie_start_drop_items", null);

            _visibleStashIndex = GetIndexOfCurrentVisibleTab();
            if (_visibleStashIndex < 0)
            {
                LogMessage($"Stshie: VisibleStashIndex was invalid: {_visibleStashIndex}, stopping.");
                yield break;
            }

            var itemsSortedByStash = _dropItems
                .OrderBy(x => x.SkipSwitchTab || x.StashIndex == _visibleStashIndex ? 0 : 1).ThenBy(x => x.StashIndex)
                .ToList();
            var waitedItems = new List<FilterResult>(8);

            Input.KeyDown(Keys.LControlKey);
            LogMessage($"Want to drop {itemsSortedByStash.Count} items.");
            foreach (var stashresult in itemsSortedByStash)
            {
                _coroutineIteration++;
                _coroutineWorker?.UpdateTicks(_coroutineIteration);
                var maxTryTime = _debugTimer.ElapsedMilliseconds + 2000;
                //move to correct tab
                if (!stashresult.SkipSwitchTab)
                    yield return SwitchToTab(stashresult.StashIndex);
                //this is shenanigans for items that take some time to get dumped like maps into maptab and divcards in divtab
                /*
                var waited = waitedItems.Count > 0;
                while (waited)
                {
                    waited = false;
                    var visibleInventoryItems = GameController.Game.IngameState.IngameUi
                                .InventoryPanel[InventoryIndex.PlayerInventory]
                                .VisibleInventoryItems;
                    foreach(var item in waitedItems)
                    {
                        if (!visibleInventoryItems.Contains(item.ItemData.InventoryItem)) continue;
                        yield return ClickElement(item.ClickPos);
                        waited = true;
                    }
                    yield return new WaitTime(Settings.ExtraDelay);
                    PublishEvent("stashie_finish_drop_items_to_stash_tab", null);
                    if (!waited) waitedItems.Clear();
                    if (_debugTimer.ElapsedMilliseconds > maxTryTime)
                    {
                        LogMessage($"Error while waiting for:{waitedItems.Count} items");
                        yield break;
                    }
                    yield return new WaitTime((int)GameController.IngameState.CurLatency); //maybe replace with Setting option
                }*/
                yield return new WaitFunctionTimed(
                    () => GameController.IngameState.IngameUi.StashElement.AllInventories[_visibleStashIndex] != null,
                    true, 2000,
                    $"Error while loading tab, Index: {_visibleStashIndex}"); //maybe replace waittime with Setting option
                yield return new WaitFunctionTimed(
                    () => GetTypeOfCurrentVisibleStash() != InventoryType.InvalidInventory,
                    true, 2000,
                    $"Error with inventory type, Index: {_visibleStashIndex}"); //maybe replace waittime with Setting option

                yield return StashItem(stashresult);

                _debugTimer.Restart();
                PublishEvent("stashie_finish_drop_items_to_stash_tab", null);
            }
        }

        private IEnumerator StashItem(FilterResult stashresult)
        {
            Input.SetCursorPos(stashresult.ClickPos + _clickWindowOffset);
            yield return new WaitTime(Settings.HoverItemDelay);
            /*
           //set cursor and update hoveritem
           yield return Settings.HoverItemDelay;

           var inventory = GameController.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory];
           while (inventory.HoverItem == null)
           {
               if (_debugTimer.ElapsedMilliseconds > maxTryTime)
               {
                   LogMessage($"Error while waiting for hover item. hoveritem is null, Index: {_visibleStashIndex}");
                   yield break;
               }
               Input.SetCursorPos(stashresult.ClickPos + _clickWindowOffset);
               yield return Settings.HoverItemDelay;
           }
           if (lastHoverItem != null)
           {
               while (inventory.HoverItem == null || inventory.HoverItem.Address == lastHoverItem.Address)
               {
                   if (_debugTimer.ElapsedMilliseconds > maxTryTime)
                   {
                       LogMessage($"Error while waiting for hover item. hoveritem is null, Index: {_visibleStashIndex}");
                       yield break;
                   }
                   Input.SetCursorPos(stashresult.ClickPos + _clickWindowOffset);
                   yield return Settings.HoverItemDelay;
               }
           }
           lastHoverItem = inventory.HoverItem;
           */
            //finally press the button
            //additional shift to circumvent affinities
            var shiftused = false;
            if (stashresult.ShiftForStashing)
            {
                Input.KeyDown(Keys.ShiftKey);
                shiftused = true;
            }

            Input.Click(MouseButtons.Left);
            if (shiftused) Input.KeyUp(Keys.ShiftKey);

            yield return new WaitTime(Settings.StashItemDelay);
        }

        #region Refill

        private IEnumerator ProcessRefills()
        {
            if (!Settings.RefillCurrency.Value || _customRefills.Count == 0) yield break;

            if (Settings.CurrencyStashTab.Index == -1)
            {
                LogError("Can't process refill: CurrencyStashTab is not set.", 5);
                yield break;
            }

            var delay = (int) GameController.Game.IngameState.CurLatency + Settings.ExtraDelay.Value;
            var currencyTabVisible = false;
            var inventory = GameController.Game.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory];
            var stashItems = inventory.VisibleInventoryItems;

            if (stashItems == null)
            {
                LogError("Can't process refill: VisibleInventoryItems is null!", 5);
                yield break;
            }

            _customRefills.ForEach(x => x.Clear());
            var filledCells = new int[5, 12];

            foreach (var inventItem in stashItems)
            {
                var item = inventItem.Item;
                if (item == null) continue;

                if (!Settings.AllowHaveMore.Value)
                {
                    var iPosX = inventItem.InventPosX;
                    var iPosY = inventItem.InventPosY;
                    var iBase = item.GetComponent<Base>();

                    for (var x = iPosX; x <= iPosX + iBase.ItemCellsSizeX - 1; x++)
                    for (var y = iPosY; y <= iPosY + iBase.ItemCellsSizeY - 1; y++)
                        if (x >= 0 && x <= 11 && y >= 0 && y <= 4)
                            filledCells[y, x] = 1;
                        else
                            LogMessage($"Out of range: {x} {y}", 10);
                }

                if (!item.HasComponent<ExileCore.PoEMemory.Components.Stack>()) continue;

                foreach (var refill in _customRefills)
                {
                    var bit = GameController.Files.BaseItemTypes.Translate(item.Path);
                    if (bit.BaseName != refill.CurrencyClass) continue;

                    var stack = item.GetComponent<ExileCore.PoEMemory.Components.Stack>();
                    refill.OwnedCount = stack.Size;
                    refill.ClickPos = inventItem.GetClientRect().Center;

                    if (refill.OwnedCount < 0 || refill.OwnedCount > 40)
                    {
                        LogError(
                            $"Ignoring refill: {refill.CurrencyClass}: Stack size {refill.OwnedCount} not in range 0-40 ",
                            5);
                        refill.OwnedCount = -1;
                    }

                    break;
                }
            }

            var inventoryRec = inventory.InventoryUIElement.GetClientRect();
            var cellSize = inventoryRec.Width / 12;
            var freeCellFound = false;
            var freeCelPos = new Point();

            if (!Settings.AllowHaveMore.Value)
                for (var x = 0; x <= 11; x++)
                {
                    for (var y = 0; y <= 4; y++)
                    {
                        if (filledCells[y, x] != 0) continue;

                        freeCellFound = true;
                        freeCelPos = new Point(x, y);
                        break;
                    }

                    if (freeCellFound) break;
                }

            foreach (var refill in _customRefills)
            {
                if (refill.OwnedCount == -1) continue;

                if (refill.OwnedCount == refill.AmountOption.Value) continue;

                if (refill.OwnedCount < refill.AmountOption.Value)

                    #region Refill

                {
                    if (!currencyTabVisible)
                    {
                        if (Settings.CurrencyStashTab.Index != _visibleStashIndex)
                        {
                            yield return SwitchToTab(Settings.CurrencyStashTab.Index);
                        }
                        else
                        {
                            currencyTabVisible = true;
                            yield return new WaitTime(delay);
                        }
                    }

                    var moveCount = refill.AmountOption.Value - refill.OwnedCount;
                    var currentStashItems = GameController.Game.IngameState.IngameUi.StashElement.VisibleStash
                        .VisibleInventoryItems;

                    var foundSourceOfRefill = currentStashItems
                        .Where(x => GameController.Files.BaseItemTypes.Translate(x.Item.Path).BaseName ==
                                    refill.CurrencyClass).ToList();

                    foreach (var sourceOfRefill in foundSourceOfRefill)
                    {
                        var stackSize = sourceOfRefill.Item.GetComponent<ExileCore.PoEMemory.Components.Stack>().Size;
                        var getCurCount = moveCount > stackSize ? stackSize : moveCount;
                        var destination = refill.ClickPos;

                        if (refill.OwnedCount == 0)
                        {
                            destination = GetInventoryClickPosByCellIndex(inventory, refill.InventPos.X,
                                refill.InventPos.Y, cellSize);

                            // If cells is not free then continue.
                            if (GameController.Game.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory][
                                refill.InventPos.X, refill.InventPos.Y, 12] != null)
                            {
                                moveCount--;
                                LogMessage(
                                    $"Inventory ({refill.InventPos.X}, {refill.InventPos.Y}) is occupied by the wrong item!",
                                    5);
                                continue;
                            }
                        }

                        yield return SplitStack(moveCount, sourceOfRefill.GetClientRect().Center, destination);
                        moveCount -= getCurCount;
                        if (moveCount == 0) break;
                    }

                    if (moveCount > 0)
                        LogMessage($"Not enough currency (need {moveCount} more) to fill {refill.CurrencyClass} stack",
                            5);
                }

                #endregion

                else if (!Settings.AllowHaveMore.Value && refill.OwnedCount > refill.AmountOption.Value)

                    #region Devastate

                {
                    if (!freeCellFound)
                    {
                        LogMessage("Can\'t find free cell in player inventory to move excess currency.", 5);
                        continue;
                    }

                    if (!currencyTabVisible)
                    {
                        if (Settings.CurrencyStashTab.Index != _visibleStashIndex)
                        {
                            yield return SwitchToTab(Settings.CurrencyStashTab.Index);
                            continue;
                        }

                        currencyTabVisible = true;
                        yield return new WaitTime(delay);
                    }

                    var destination = GetInventoryClickPosByCellIndex(inventory, freeCelPos.X, freeCelPos.Y, cellSize) +
                                      _clickWindowOffset;
                    var moveCount = refill.OwnedCount - refill.AmountOption.Value;
                    yield return new WaitTime(delay);
                    yield return SplitStack(moveCount, refill.ClickPos, destination);
                    yield return new WaitTime(delay);
                    Input.KeyDown(Keys.LControlKey);

                    yield return Input.SetCursorPositionSmooth(destination + _clickWindowOffset);
                    yield return new WaitTime(Settings.ExtraDelay);
                    Input.Click(MouseButtons.Left);
                    Input.MouseMove();
                    Input.KeyUp(Keys.LControlKey);
                    yield return new WaitTime(delay);
                }

                #endregion
            }
        }

        private static Vector2 GetInventoryClickPosByCellIndex(Inventory inventory, int indexX, int indexY,
            float cellSize)
        {
            return inventory.InventoryUIElement.GetClientRect().TopLeft +
                   new Vector2(cellSize * (indexX + 0.5f), cellSize * (indexY + 0.5f));
        }

        private IEnumerator SplitStack(int amount, Vector2 from, Vector2 to)
        {
            var delay = (int) GameController.Game.IngameState.CurLatency * 2 + Settings.ExtraDelay;
            Input.KeyDown(Keys.ShiftKey);

            while (!Input.IsKeyDown(Keys.ShiftKey)) yield return new WaitTime(WhileDelay);

            yield return Input.SetCursorPositionSmooth(from + _clickWindowOffset);
            yield return new WaitTime(Settings.ExtraDelay);
            Input.Click(MouseButtons.Left);
            Input.MouseMove();
            yield return new WaitTime(InputDelay);
            Input.KeyUp(Keys.ShiftKey);
            yield return new WaitTime(InputDelay + 50);

            if (amount > 40)
            {
                LogMessage("Can't select amount more than 40, current value: " + amount, 5);
                amount = 40;
            }

            if (amount < 10)
            {
                var keyToPress = (int) Keys.D0 + amount;
                yield return Input.KeyPress((Keys) keyToPress);
            }
            else
            {
                var keyToPress = (int) Keys.D0 + amount / 10;
                yield return Input.KeyPress((Keys) keyToPress);
                yield return new WaitTime(delay);
                keyToPress = (int) Keys.D0 + amount % 10;
                yield return Input.KeyPress((Keys) keyToPress);
            }

            yield return new WaitTime(delay);
            yield return Input.KeyPress(Keys.Enter);
            yield return new WaitTime(delay + InputDelay);

            yield return Input.SetCursorPositionSmooth(to + _clickWindowOffset);
            yield return new WaitTime(Settings.ExtraDelay);
            Input.Click(MouseButtons.Left);

            yield return new WaitTime(delay + InputDelay);
        }

        #endregion

        #region Switching between StashTabs

        public IEnumerator SwitchToTab(int tabIndex)
        {
            // We don't want to Switch to a tab that we are already on or that has the magic number for affinities
            //var stashPanel = GameController.Game.IngameState.IngameUi.StashElement;

            _visibleStashIndex = GetIndexOfCurrentVisibleTab();
            var travelDistance = Math.Abs(tabIndex - _visibleStashIndex);
            if (travelDistance == 0) yield break;

            if (Settings.AlwaysUseArrow.Value || travelDistance < 2 || !SliderPresent())
                yield return SwitchToTabViaArrowKeys(tabIndex);
            else
                yield return SwitchToTabViaDropdownMenu(tabIndex);

            yield return Delay();
        }

        private IEnumerator SwitchToTabViaArrowKeys(int tabIndex, int numberOfTries = 1)
        {
            if (numberOfTries >= 3) yield break;

            var indexOfCurrentVisibleTab = GetIndexOfCurrentVisibleTab();
            var travelDistance = tabIndex - indexOfCurrentVisibleTab;
            var tabIsToTheLeft = travelDistance < 0;
            travelDistance = Math.Abs(travelDistance);

            if (tabIsToTheLeft)
                yield return PressKey(Keys.Left, travelDistance);
            else
                yield return PressKey(Keys.Right, travelDistance);

            if (GetIndexOfCurrentVisibleTab() != tabIndex)
            {
                yield return Delay(20);
                yield return SwitchToTabViaArrowKeys(tabIndex, numberOfTries + 1);
            }
        }

        private IEnumerator PressKey(Keys key, int repetitions = 1)
        {
            for (var i = 0; i < repetitions; i++) yield return Input.KeyPress(key);
        }

        private bool DropDownMenuIsVisible()
        {
            return GameController.Game.IngameState.IngameUi.StashElement.ViewAllStashPanel.IsVisible;
        }

        private IEnumerator OpenDropDownMenu()
        {
            var button = GameController.Game.IngameState.IngameUi.StashElement.ViewAllStashButton.GetClientRect();
            yield return ClickElement(button.Center);
            while (!DropDownMenuIsVisible()) yield return Delay(1);
        }

        private static bool StashLabelIsClickable(int index)
        {
            return index + 1 < MaxShownSidebarStashTabs;
        }

        private bool SliderPresent()
        {
            return _stashCount > MaxShownSidebarStashTabs;
        }

        private IEnumerator ClickDropDownMenuStashTabLabel(int tabIndex)
        {
            var dropdownMenu = GameController.Game.IngameState.IngameUi.StashElement.ViewAllStashPanel;
            var stashTabLabels = dropdownMenu.GetChildAtIndex(1);

            //if the stash tab index we want to visit is less or equal to 30, then we scroll all the way to the top.
            //scroll amount (clicks) should always be (stash_tab_count - 31);
            //TODO(if the guy has more than 31*2 tabs and wants to visit stash tab 32 fx, then we need to scroll all the way up (or down) and then scroll 13 clicks after.)

            var clickable = StashLabelIsClickable(tabIndex);
            // we want to go to stash 32 (index 31).
            // 44 - 31 = 13
            // 31 + 45 - 44 = 30
            // MaxShownSideBarStashTabs + _stashCount - tabIndex = index
            var index = clickable ? tabIndex : tabIndex - (_stashCount - 1 - (MaxShownSidebarStashTabs - 1));
            var pos = stashTabLabels.GetChildAtIndex(index).GetClientRect().Center;
            MoveMouseToElement(pos);
            if (SliderPresent())
            {
                var clicks = _stashCount - MaxShownSidebarStashTabs;
                yield return Delay(3);
                VerticalScroll(clickable, clicks);
                yield return Delay(3);
            }

            DebugWindow.LogMsg($"Stashie: Moving to tab '{tabIndex}'.", 3, Color.LightGray);
            yield return Click();
        }

        private IEnumerator ClickElement(Vector2 pos, MouseButtons mouseButton = MouseButtons.Left)
        {
            MoveMouseToElement(pos);
            yield return Click(mouseButton);
        }

        private IEnumerator Click(MouseButtons mouseButton = MouseButtons.Left)
        {
            Input.Click(mouseButton);
            yield return Delay();
        }

        private void MoveMouseToElement(Vector2 pos)
        {
            Input.SetCursorPos(pos + GameController.Window.GetWindowRectangle().TopLeft);
        }

        private IEnumerator Delay(int ms = 0)
        {
            yield return new WaitTime(Settings.ExtraDelay.Value + ms);
        }

        private IEnumerator SwitchToTabViaDropdownMenu(int tabIndex)
        {
            if (!DropDownMenuIsVisible()) yield return OpenDropDownMenu();

            yield return ClickDropDownMenuStashTabLabel(tabIndex);
        }

        private int GetIndexOfCurrentVisibleTab()
        {
            return GameController.Game.IngameState.IngameUi.StashElement.IndexVisibleStash;
        }

        private InventoryType GetTypeOfCurrentVisibleStash()
        {
            var stashPanelVisibleStash = GameController.Game.IngameState.IngameUi?.StashElement?.VisibleStash;
            return stashPanelVisibleStash?.InvType ?? InventoryType.InvalidInventory;
        }

        #endregion

        #region Stashes update

        private void OnSettingsStashNameChanged(ListIndexNode node, string newValue)
        {
            node.Index = GetInventIndexByStashName(newValue);
        }

        public override void OnClose()
        {
        }

        private void SetupOrClose()
        {
            SaveDefaultConfigsToDisk();
            _settingsListNodes = new List<ListIndexNode>(100);
            LoadCustomRefills();
            LoadCustomFilters();

            try
            {
                Settings.TabToVisitWhenDone.Max =
                    (int) GameController.Game.IngameState.IngameUi.StashElement.TotalStashes - 1;
                var names = GameController.Game.IngameState.IngameUi.StashElement.AllStashNames;
                UpdateStashNames(names);
            }
            catch (Exception e)
            {
                LogError($"Cant get stash names when init. {e}");
            }
        }

        private int GetInventIndexByStashName(string name)
        {
            var index = _renamedAllStashNames.IndexOf(name);
            if (index != -1) index--;

            return index;
        }

        private List<string> _renamedAllStashNames;

        private void UpdateStashNames(ICollection<string> newNames)
        {
            Settings.AllStashNames = newNames.ToList();

            if (newNames.Count < 4)
            {
                LogError("Can't parse names.");
                return;
            }

            _renamedAllStashNames = new List<string> {"Ignore"};
            var settingsAllStashNames = Settings.AllStashNames;

            for (var i = 0; i < settingsAllStashNames.Count; i++)
            {
                var realStashName = settingsAllStashNames[i];

                if (_renamedAllStashNames.Contains(realStashName))
                {
                    realStashName += " (" + i + ")";
#if DebugMode
                    LogMessage("Stashie: fixed same stash name to: " + realStashName, 3);
#endif
                }

                _renamedAllStashNames.Add(realStashName ?? "%NULL%");
            }

            Settings.AllStashNames.Insert(0, "Ignore");

            foreach (var lOption in _settingsListNodes)
                try
                {
                    lOption.SetListValues(_renamedAllStashNames);
                    var inventoryIndex = GetInventIndexByStashName(lOption.Value);

                    if (inventoryIndex == -1) //If the value doesn't exist in list (renamed)
                    {
                        if (lOption.Index != -1) //If the value doesn't exist in list and the value was not Ignore
                        {
#if DebugMode
                        LogMessage("Tab renamed : " + lOption.Value + " to " + _renamedAllStashNames[lOption.Index + 1],
                            5);
#endif
                            if (lOption.Index + 1 >= _renamedAllStashNames.Count)
                            {
                                lOption.Index = -1;
                                lOption.Value = _renamedAllStashNames[0];
                            }
                            else
                            {
                                lOption.Value = _renamedAllStashNames[lOption.Index + 1]; //    Just update it's name
                            }
                        }
                        else
                        {
                            lOption.Value =
                                _renamedAllStashNames[0]; //Actually it was "Ignore", we just update it (can be removed)
                        }
                    }
                    else //tab just change it's index
                    {
#if DebugMode
                    if (lOption.Index != inventoryIndex)
                    {
                        LogMessage("Tab moved: " + lOption.Index + " to " + inventoryIndex, 5);
                    }
#endif
                        lOption.Index = inventoryIndex;
                        lOption.Value = _renamedAllStashNames[inventoryIndex + 1];
                    }
                }
                catch (Exception e)
                {
                    DebugWindow.LogError($"UpdateStashNames _settingsListNodes {e}");
                }

            GenerateMenu();
        }

        private static readonly WaitTime Wait2Sec = new WaitTime(2000);
        private static readonly WaitTime Wait1Sec = new WaitTime(1000);
        private uint _counterStashTabNamesCoroutine;

        public IEnumerator StashTabNamesUpdater_Thread()
        {
            while (true)
            {
                while (!GameController.Game.IngameState.InGame) yield return Wait2Sec;

                var stashPanel = GameController.Game.IngameState?.IngameUi?.StashElement;

                while (stashPanel == null || !stashPanel.IsVisibleLocal) yield return Wait1Sec;

                _counterStashTabNamesCoroutine++;
                _stashTabNamesCoroutine?.UpdateTicks(_counterStashTabNamesCoroutine);
                var cachedNames = Settings.AllStashNames;
                var realNames = stashPanel.AllStashNames;

                if (realNames.Count + 1 != cachedNames.Count)
                {
                    UpdateStashNames(realNames);
                    continue;
                }

                for (var index = 0; index < realNames.Count; ++index)
                {
                    var cachedName = cachedNames[index + 1];
                    if (cachedName.Equals(realNames[index])) continue;

                    UpdateStashNames(realNames);
                    break;
                }

                yield return Wait1Sec;
            }
        }

        private static void VerticalScroll(bool scrollUp, int clicks)
        {
            const int wheelDelta = 120;
            if (scrollUp)
                WinApi.mouse_event(Input.MOUSE_EVENT_WHEEL, 0, 0, clicks * wheelDelta, 0);
            else
                WinApi.mouse_event(Input.MOUSE_EVENT_WHEEL, 0, 0, -(clicks * wheelDelta), 0);
        }

        #endregion
    }
}