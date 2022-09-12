using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HarmonyLib;

using TerraTech.Network;

namespace SaveTranslator {

    [HarmonyPatch(typeof(ManSaveGame.SaveData), "Deserialize")]
    internal class Patches {

        internal static int VanillaIDs = Enum.GetValues(typeof(BlockTypes)).Length;

        internal static ManMods manMods = Singleton.Manager<ManMods>.inst;
        internal static ManLicenses manLicenses = Singleton.Manager<ManLicenses>.inst;
        internal static RecipeManager recipeManager = Singleton.Manager<RecipeManager>.inst;

        internal static readonly FieldInfo m_CurrentSession = AccessTools.Field(typeof(ManMods), "m_CurrentSession");
        internal static readonly MethodInfo SetBlockState = AccessTools.Method(typeof(ManLicenses), "SetBlockState");
        internal static readonly FieldInfo m_ModdedRecipes = AccessTools.Field(typeof(RecipeManager), "m_ModdedRecipes");
        internal static readonly FieldInfo m_SaveDataJSON = AccessTools.Field(typeof(ManSaveGame.SaveData), "m_SaveDataJSON");


        [HarmonyPostfix]
        internal static void Postfix(ref ManSaveGame.SaveData __instance, StreamReader streamReader, bool loadInfoOnly, bool assertOnFail, bool validate) {

            SaveDataPlayer playerData = null;

            SaveTranslatorMod.logger.Trace($"getSaveData and !loadInfoOnly {loadInfoOnly}");
            //We only want to translate the actual save data, so don't do anything if only loading the save info
            if(!loadInfoOnly && __instance.SaveInfo.m_GameType == ManGameMode.GameType.MainGame && __instance.State.GetSaveData<SaveDataPlayer>(ManSaveGame.SaveDataJSONType.ManPlayer, out playerData) && playerData != null) {

                //Get all the things we need so we can translate stuff
                ModSessionInfo currentSession = (ModSessionInfo)m_CurrentSession.GetValue(manMods);

                //var a = Traverse.Create(manLicenses).Method("SetBlockState", new Type[] { typeof(BlockTypes), typeof(ManLicenses.BlockState) });

                SaveTranslatorMod.logger.Trace("Attempting to get m_SaveDataJSON");
                var stat = Traverse.Create(__instance).Field("m_State");
                SaveTranslatorMod.logger.Trace("Traverse1");
                Dictionary<string, string> SaveDataJSON = (Dictionary<string, string>)stat.Field("m_SaveDataJSON").GetValue();
                SaveTranslatorMod.logger.Trace("Got the SavaDataJSON!!!!");

                SaveTranslatorMod.logger.Trace($"Attempting to get blockStates!!");
                JObject manLicenseJSON = JsonConvert.DeserializeObject<JObject>(SaveDataJSON["ManLicenses"]);
                Dictionary<string, int> blockStatesJSON = manLicenseJSON["m_BlockStates"].ToObject<Dictionary<string, int>>();
                Dictionary<string, int> translatedBlockStates = new Dictionary<string, int>();

                ModSessionInfo info = null;
                __instance.State.GetSaveData<ModSessionInfo>(ManSaveGame.SaveDataJSONType.ManMods, out info);


                Dictionary<int, string> SessionBlockIDs = new Dictionary<int, string>(currentSession.BlockIDs);
                foreach(BlockTypes block in Enum.GetValues(typeof(BlockTypes)))
                    SessionBlockIDs.Add((int)block, block.ToString());

                Dictionary<string, int> SwappedSessionBlockIDs = new Dictionary<string, int>();
                foreach(KeyValuePair<int, string> block in SessionBlockIDs) {
                    if(block.Key > VanillaIDs) {
                        ModdedBlockDefinition blockDefinition = Singleton.Manager<ManMods>.inst.FindModdedAsset<ModdedBlockDefinition>(block.Value);
                        SaveTranslatorMod.logger.Trace($"BlockDefinition for [{block.Value}] grade [{blockDefinition.m_Grade}] corp [{blockDefinition.m_Corporation}] licenseUnlock [{blockDefinition.m_UnlockWithLicense}]");
                    }
                    SwappedSessionBlockIDs.Add(block.Value, block.Key);
                }


                Dictionary<string, int> SwappedSaveBlockIDs = new Dictionary<string, int>();
                foreach(KeyValuePair<int, string> block in info.BlockIDs)
                    SwappedSaveBlockIDs.Add(block.Value, block.Key);

                IInventory<BlockTypes> newInvent = new SingleplayerInventory();

                Dictionary<int, int> tempInventory = new Dictionary<int, int>();
                foreach(KeyValuePair<BlockTypes, int> block in playerData.m_Inventory)
                    tempInventory.Add((int)block.Key, block.Value);

                bool saveIDToSessionID(int blockID, out int newBlockID) {
                    newBlockID = blockID;
                    if(blockID < VanillaIDs)
                        return true;

                    if(info.BlockIDs.ContainsKey(blockID)) {
                        if(SwappedSessionBlockIDs.ContainsKey(info.BlockIDs[blockID])) {
                            newBlockID = SwappedSessionBlockIDs[info.BlockIDs[blockID]];
                            return true;
                        }
                    }
                    return false;
                }

                //Player Inventory processing loop

                foreach(KeyValuePair<string, int> block in SwappedSessionBlockIDs) {
                    int blockIntID = block.Value;
                    int saveBlockIntID;

                    string blockName = block.Key;

                    if(SwappedSaveBlockIDs.ContainsKey(blockName) || blockIntID <= VanillaIDs) {
                        if(blockIntID > VanillaIDs) {
                            saveBlockIntID = SwappedSaveBlockIDs[blockName];
                        } else {
                            saveBlockIntID = block.Value;
                        }
                        SaveTranslatorMod.logger.Trace($"Block {blockName} with save ID[{saveBlockIntID}] sessionID[{blockIntID}] is either vanilla or in old save!");

                        if(tempInventory.ContainsKey(saveBlockIntID)) {
                            SaveTranslatorMod.logger.Trace($"Found Block {blockName} in save inventory");
                            newInvent.SetBlockCount((BlockTypes)blockIntID, tempInventory[saveBlockIntID]);
                        }

                        if(blockStatesJSON.ContainsKey(saveBlockIntID.ToString())) {

                            //Singleton.Manager<ManLicenses>.inst.m_UnlockTable.GetCorpBlockData(16).
                            SaveTranslatorMod.logger.Trace($"✔️ Found Block {blockName} in save blockstates");
                            SaveTranslatorMod.logger.Trace($"Block {blockName} state:[{blockStatesJSON[saveBlockIntID.ToString()]}]");
                            translatedBlockStates.Add(blockIntID.ToString(), blockStatesJSON[saveBlockIntID.ToString()]);
                            SaveTranslatorMod.logger.Trace($"Added Block {blockName} with state of [{translatedBlockStates[blockIntID.ToString()]}]");
                        } else {
                            SaveTranslatorMod.logger.Trace($"❌ Block {blockName} was not found in save blockstates");
                            //translatedBlockStates.Add(blockIntID.ToString(), 1);
                        }
                    } else {
                        SaveTranslatorMod.logger.Trace($"Block [{blockName}][{block.Value}] is not vanilla or in save data?");
                    }
                }

                SaveTranslatorMod.logger.Trace("begin shop processing!");
                if(__instance.State.m_StoredTilesJSON != null) {
                    SaveTranslatorMod.logger.Trace("Found tiles to go through!");
                    List<IntVector2> keys = __instance.State.m_StoredTilesJSON.Keys.ToList<IntVector2>();

                    for(int i = 0; i < keys.Count; i++) {
                        KeyValuePair<IntVector2, string> storedTileKV = new KeyValuePair<IntVector2, string>(keys[i], __instance.State.m_StoredTilesJSON[keys[i]]);

                        SaveTranslatorMod.logger.Trace($"Checking tile [{storedTileKV.Key}] for shops");

                        ManSaveGame.StoredTile storedTile = null;
                        ManSaveGame.LoadObjectFromRawJson<ManSaveGame.StoredTile>(ref storedTile, storedTileKV.Value, false, false);
                        if(storedTile == null) {
                            SaveTranslatorMod.logger.Trace($"WARNING storedTile is null!");
                            continue;
                        }
                        if(storedTile.m_StoredVisibles == null) {
                            SaveTranslatorMod.logger.Trace($"WARNING no stored visibles!");
                            continue;
                        }
                        if(storedTile.m_StoredVisibles.Count() < 1) {
                            SaveTranslatorMod.logger.Trace($"WARNING no count of stored visibles! {storedTile.m_StoredVisibles.Count()}");
                            continue;
                        }
                        if(!storedTile.m_StoredVisibles.ContainsKey(1)) {
                            SaveTranslatorMod.logger.Trace($"WARNING storedVisibles has no techs/shops! {storedTile.m_StoredVisibles.Keys}");
                            continue;
                        }

                        JObject storedTileJSON = JsonConvert.DeserializeObject<JObject>(storedTileKV.Value);
                        JObject m_StoredVisibles = (JObject)storedTileJSON["m_StoredVisibles"];
                        JArray m_StoredVisibles2 = (JArray)m_StoredVisibles["1"];
                        foreach(JToken storedVis in m_StoredVisibles2.Children()) {

                            string nameForJSONChecking = "";
                            SaveTranslatorMod.logger.Trace($"testing json name");
                            if((storedVis["m_TechData"]["m_LocalisedName"]).ToString() != "")
                                nameForJSONChecking = (storedVis["m_TechData"]["m_LocalisedName"]["m_Id"]).ToObject<string>();
                            else
                                nameForJSONChecking = (storedVis["m_TechData"]["m_Name"]).ToObject<string>();

                            //if(nameForJSONChecking == "GSOTradingStation") {
                            SaveTranslatorMod.logger.Trace($"made it to inner loop with name {nameForJSONChecking}");
                            JArray blockSpecs = (JArray)storedVis["m_TechData"]["m_BlockSpecs"];
                            foreach(JToken blockSpec in blockSpecs.Children()) {
                                SaveTranslatorMod.logger.Trace("Searching through TechData's BlockSpecs");

                                if(blockSpec["saveState"].SelectToken("353829868") != null) {
                                    SaveTranslatorMod.logger.Trace($"Found a Fabricator! Checking for recipe");
                                    if(blockSpec["saveState"]["353829868"]["consumeProgress"]["currentRecipe"].Type != JTokenType.Null) {
                                        SaveTranslatorMod.logger.Trace("Found a recipe!");
                                        JArray outputItems = (JArray)blockSpec["saveState"]["353829868"]["consumeProgress"]["currentRecipe"]["m_OutputItems"];
                                        SaveTranslatorMod.logger.Trace("Got its output items");
                                        foreach(JToken outputItem in outputItems.Children()) {
                                            SaveTranslatorMod.logger.Trace("checking output item");
                                            if(saveIDToSessionID(outputItem["m_Item"]["ItemType"].ToObject<int>(), out int blockIntID)) {
                                                SaveTranslatorMod.logger.Trace("✔️ Found output item in current game, fixing");
                                                outputItem["m_Item"]["ItemType"] = blockIntID;
                                                outputItem["m_Item"]["name"] = blockIntID.ToString();
                                            }
                                        }

                                    } else
                                        SaveTranslatorMod.logger.Trace("Fab has no recipe currently set so don't need to translate!");
                                }

                                if(blockSpec["m_BlockType"].ToObject<int>() == 557) {
                                    SaveTranslatorMod.logger.Trace($"found the vendor blocktype in the json!");
                                    JArray inventoryJSON = (JArray)blockSpec["saveState"]["1840377074"]["supplierSaveData"]["inventory"]["m_InventoryList"];

                                    Dictionary<int, int> newInv = new Dictionary<int, int>();
                                    foreach(JToken invBlock in inventoryJSON.Children())
                                        newInv.Add(invBlock["m_BlockType"].ToObject<int>(), invBlock["m_Quantity"].ToObject<int>());

                                    inventoryJSON.Clear();

                                    foreach(KeyValuePair<int, int> block in newInv) {
                                        if(saveIDToSessionID(block.Key, out int blockIntID)) {
                                            SaveTranslatorMod.logger.Trace($"✔️ Found Block {SessionBlockIDs[blockIntID]} in save inventory");
                                            string jsonString = @"{ 'm_BlockType':" + blockIntID + ", 'm_Quantity':" + newInv[block.Key] + "}";
                                            SaveTranslatorMod.logger.Trace($"JSON string for adding back to invent json [{jsonString}]");
                                            inventoryJSON.Add(JToken.Parse(jsonString));
                                        }
                                    }
                                }
                            }
                            //}
                        }
                        __instance.State.m_StoredTilesJSON[storedTileKV.Key] = storedTileJSON.ToString(Formatting.None);
                    }
                } else {
                    SaveTranslatorMod.logger.Trace("ERROR no stored tiles to go through!");
                }
                SaveTranslatorMod.logger.Trace("end shop processing!");

                SaveTranslatorMod.logger.Trace("Attempting to save m_SaveDataJSON!");
                SaveTranslatorMod.logger.Trace("translatedBlockStates to manLicenseJSON[\"m_BlockStates\"]");
                manLicenseJSON["m_BlockStates"] = JObject.FromObject(translatedBlockStates);
                SaveTranslatorMod.logger.Trace("manLicenseJSON[\"m_BlockStates\"] to SaveDataJSON[\"ManLicenses\"]");
                SaveDataJSON["ManLicenses"] = JsonConvert.SerializeObject(manLicenseJSON);
                SaveTranslatorMod.logger.Trace("SaveDataJSON[\"ManLicenses\"] to stat.Field(\"m_SaveDataJSON\")");
                stat.Field("m_SaveDataJSON").SetValue(SaveDataJSON);
                SaveTranslatorMod.logger.Trace("m_SaveDataJSON done!");

                //Saving back data
                playerData.m_Inventory = newInvent;
                __instance.State.AddSaveData<SaveDataPlayer>(ManSaveGame.SaveDataJSONType.ManPlayer, playerData);

            }
        }

        private class SaveDataPlayer {
            // Token: 0x04003D74 RID: 15732
            public bool m_PaletteUnlocked;

            // Token: 0x04003D75 RID: 15733
            public int m_Money;

            // Token: 0x04003D76 RID: 15734
            [JsonConverter(typeof(InventoryJsonConverter))]
            public IInventory<BlockTypes> m_Inventory = new SingleplayerInventory();

            // Token: 0x04003D77 RID: 15735
            public List<int> m_TrackedIDs = new List<int>();

            // Token: 0x04003D78 RID: 15736
            public ManPlayer.HotswapMap m_HotswapMap;

            // Token: 0x04003D79 RID: 15737
            public bool m_PlayerIndestructible;

            // Token: 0x04003D7A RID: 15738
            public bool m_SkipPowerupSequencing;

            // Token: 0x04003D7B RID: 15739
            public List<Patches.PlayerSaveData> m_Players = new List<Patches.PlayerSaveData>(4);
        }
        private class PlayerSaveData {
            // Token: 0x04003D7C RID: 15740
            [JsonConverter(typeof(TTNetworkIDConverter))]
            public TTNetworkID m_NetID;

            // Token: 0x04003D7D RID: 15741
            public int m_TrackedVisibleIDOfLastTech;
        }
    }
}
