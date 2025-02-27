using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Linq;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using System;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Entity;

namespace LocationLoader
{
    public class LocationLoader : MonoBehaviour
    {
        HashSet<int> visitedRegions = new HashSet<int>();
        Dictionary<Vector2Int, List<LocationInstance>> worldPixelInstances = new Dictionary<Vector2Int, List<LocationInstance>>();
        Dictionary<int, Dictionary<string, Mod>> modRegionFiles = new Dictionary<int, Dictionary<string, Mod>>();

        Dictionary<string, Mod> modLocationPrefabs = null;
        Dictionary<string, LocationPrefab> prefabInfos = new Dictionary<string, LocationPrefab>();
        Dictionary<string, GameObject> prefabTemplates = new Dictionary<string, GameObject>();

        Dictionary<Vector2Int, WeakReference<DaggerfallTerrain>> loadedTerrain = new Dictionary<Vector2Int, WeakReference<DaggerfallTerrain>>();
        Dictionary<Vector2Int, List<LocationData>> pendingType2Locations = new Dictionary<Vector2Int, List<LocationData>>();
        Dictionary<ulong, List<Vector2Int>> type2InstancePendingTerrains = new Dictionary<ulong, List<Vector2Int>>();

        public const int TERRAIN_SIZE = 128;
        public const int ROAD_WIDTH = 4; // Actually 2, but let's leave a bit of a gap   
        public const float TERRAINPIXELSIZE = 819.2f;
        public const float TERRAIN_SIZE_MULTI = TERRAINPIXELSIZE / TERRAIN_SIZE;

        bool sceneLoading = false; 

        void Start()
        {
            Debug.Log("Begin mod init: Location Loader");

            LocationConsole.RegisterCommands();
            CacheGlobalInstances();

            Debug.Log("Finished mod init: Location Loader");
        }

        private void OnEnable()
        {
            DaggerfallTerrain.OnPromoteTerrainData += AddLocation;
            StreamingWorld.OnInitWorld += StreamingWorld_OnInitWorld;
            StreamingWorld.OnUpdateTerrainsEnd += StreamingWorld_OnUpdateTerrainsEnd;
        }

        private void OnDisable()
        {
            DaggerfallTerrain.OnPromoteTerrainData -= AddLocation;
            StreamingWorld.OnInitWorld -= StreamingWorld_OnInitWorld;
            StreamingWorld.OnUpdateTerrainsEnd -= StreamingWorld_OnUpdateTerrainsEnd;
        }

        private void StreamingWorld_OnInitWorld()
        {
            sceneLoading = true;
        }

        private void StreamingWorld_OnUpdateTerrainsEnd()
        {
            if(sceneLoading)
            {
                StartCoroutine(InstantiateAllDynamicObjectsNextFrame());
            }
        }

        System.Collections.IEnumerator InstantiateAllDynamicObjectsNextFrame()
        {
            yield return new WaitForEndOfFrame();

            var instances = FindObjectsOfType<LocationData>();
            foreach (var instance in instances)
            {
                InstantiateInstanceDynamicObjects(instance.gameObject, instance.Location, instance.Prefab);
            }

            sceneLoading = false;

            yield break;
        }

        public bool TryGetTerrain(int worldX, int worldY, out DaggerfallTerrain terrain)
        {
            var worldCoord = new Vector2Int(worldX, worldY);
            if (loadedTerrain.TryGetValue(worldCoord, out WeakReference<DaggerfallTerrain> terrainReference))
            {
                if(terrainReference.TryGetTarget(out terrain))
                {
                    // Terrain has been pooled and placed somewhere else
                    // Happens with Distant Terrain
                    if(terrain.MapPixelX != worldX || terrain.MapPixelY != worldY)
                    {
                        loadedTerrain.Remove(worldCoord);
                        return false;
                    }
                    return true;
                }
                else
                {
                    loadedTerrain.Remove(worldCoord);
                }
            }

            terrain = null;
            return false;
        }

        void CacheLocationPrefabs()
        {
            if (modLocationPrefabs != null)
                return;

            modLocationPrefabs = new Dictionary<string, Mod>();

            foreach (Mod mod in ModManager.Instance.Mods)
            {
                if (!mod.Enabled)
                    continue;

                if (mod.AssetBundle && mod.AssetBundle.GetAllAssetNames().Length > 0)
                {
                    string dummyFilePath = mod.AssetBundle.GetAllAssetNames()[0];
                    string modFolderPrefix = dummyFilePath.Substring(17);
                    modFolderPrefix = dummyFilePath.Substring(0, 17 + modFolderPrefix.IndexOf('/'));

                    string prefabFolder = modFolderPrefix + "/locations/locationprefab/";

                    foreach (string filename in mod.AssetBundle.GetAllAssetNames()
                        .Where(file => file.StartsWith(prefabFolder, StringComparison.InvariantCultureIgnoreCase)
                        && (file.EndsWith(".txt", StringComparison.InvariantCultureIgnoreCase)))
                        .Select(file => Path.GetFileName(file).ToLower()))
                    {
                        modLocationPrefabs[filename] = mod;
                    }
                }
#if UNITY_EDITOR
                else if (mod.IsVirtual && mod.ModInfo.Files.Count > 0)
                {
                    string dummyFilePath = mod.ModInfo.Files[0];
                    string modFolderPrefix = dummyFilePath.Substring(17);
                    modFolderPrefix = dummyFilePath.Substring(0, 17 + modFolderPrefix.IndexOf('/'));

                    string prefabFolder = modFolderPrefix + "/Locations/LocationPrefab/";

                    foreach (string filename in mod.ModInfo.Files
                        .Where(file => file.StartsWith(prefabFolder, StringComparison.InvariantCultureIgnoreCase)
                        && (file.EndsWith(".txt", StringComparison.InvariantCultureIgnoreCase)))
                        .Select(file => Path.GetFileName(file).ToLower()))
                    {
                        modLocationPrefabs[filename] = mod;
                    }
                }
#endif                
            }

            string looseLocationFolder = Path.Combine(Application.dataPath, LocationHelper.locationInstanceFolder);
            string looseLocationPrefabFolder = Path.Combine(looseLocationFolder, "LocationPrefab");
            bool hasLooseFiles = Directory.Exists(looseLocationFolder) && Directory.Exists(looseLocationPrefabFolder);
            if(hasLooseFiles)
            {
                foreach(string filename in Directory.GetFiles(looseLocationFolder)
                    .Where(file => file.EndsWith(".txt", StringComparison.InvariantCultureIgnoreCase))
                    .Select(file => Path.GetFileName(file).ToLower()))
                {
                    modLocationPrefabs[filename] = null;
                }
            }
        }

        LocationPrefab GetPrefabInfo(string prefabName)
        {
            CacheLocationPrefabs();

            string assetName = prefabName + ".txt";

            LocationPrefab prefabInfo;
            if (!prefabInfos.TryGetValue(assetName, out prefabInfo))
            {
                Mod mod;
                if (!modLocationPrefabs.TryGetValue(assetName.ToLower(), out mod))
                {
                    Debug.LogWarning($"Can't find location prefab '{prefabName}'");
                    return null;
                }

                prefabInfo = mod != null
                    ? LocationHelper.LoadLocationPrefab(mod, assetName)
                    : LocationHelper.LoadLocationPrefab(Application.dataPath + LocationHelper.locationPrefabFolder + assetName);

                if (prefabInfo == null)
                {
                    Debug.LogWarning($"Location prefab '{prefabName}' could not be parsed");
                    return null;
                }

                prefabInfos.Add(assetName, prefabInfo);
            }

            return prefabInfo;
        }

        void InstantiateInstanceDynamicObjects(GameObject instance, LocationInstance loc, LocationPrefab locationPrefab)
        {
            foreach (LocationObject obj in locationPrefab.obj)
            {
                if (!IsDynamicObject(obj))
                    continue;

                GameObject go = null;

                if (obj.type == 2)
                {
                    string[] arg = obj.name.Split('.');

                    if (arg[0] == "199")
                    {
                        switch (arg[1])
                        {
                            case "16":
                                if (!int.TryParse(obj.extraData, out int enemyID))
                                {
                                    Debug.LogError($"Could not spawn enemy, invalid extra data '{obj.extraData}'");
                                    break;
                                }

                                if (!Enum.IsDefined(typeof(MobileTypes), enemyID))
                                {
                                    Debug.LogError($"Could not spawn enemy, unknown mobile type '{obj.extraData}'");
                                    break;
                                }

                                ulong v = (uint)obj.objectID;
                                ulong loadId = (loc.locationID << 16) | v;

                                // Enemy is dead, don't spawn anything
                                if (LocationModLoader.modObject.GetComponent<LocationSaveDataInterface>().IsEnemyDead(loadId))
                                {
                                    break;
                                }

                                MobileTypes mobileType = (MobileTypes)enemyID;
                                go = GameObjectHelper.CreateEnemy(TextManager.Instance.GetLocalizedEnemyName((int)mobileType), mobileType, obj.pos, MobileGender.Unspecified, instance.transform);
                                SerializableEnemy serializable = go.GetComponent<SerializableEnemy>();
                                if(serializable != null)
                                {
                                    Destroy(serializable);
                                }

                                DaggerfallEntityBehaviour behaviour = go.GetComponent<DaggerfallEntityBehaviour>();
                                EnemyEntity entity = (EnemyEntity)behaviour.Entity;
                                if(entity.MobileEnemy.Gender == MobileGender.Male)
                                {
                                    entity.Gender = Genders.Male;
                                }
                                else if(entity.MobileEnemy.Gender == MobileGender.Female)
                                {
                                    entity.Gender = Genders.Female;
                                }

                                DaggerfallEnemy enemy = go.GetComponent<DaggerfallEnemy>();
                                if (enemy != null)
                                {
                                    enemy.LoadID = loadId;
                                    go.AddComponent<LocationEnemySerializer>();
                                }
                                break;

                            case "19":
                                {
                                    int record = UnityEngine.Random.Range(0, 48);
                                    go = LocationHelper.CreateLootContainer(loc.locationID, obj.objectID, 216, record, instance.transform);
                                    go.transform.localPosition = obj.pos;
                                    break;
                                }
                        }
                    }
                }

                if (go != null)
                {
                    if (go.GetComponent<DaggerfallBillboard>())
                    {
                        float tempY = go.transform.position.y;
                        go.GetComponent<DaggerfallBillboard>().AlignToBase();
                        go.transform.position = new Vector3(go.transform.position.x, tempY + ((go.transform.position.y - tempY) * go.transform.localScale.y), go.transform.position.z);
                    }
                }
            }
        }

        Vector3 GetLocationPosition(LocationInstance loc, DaggerfallTerrain daggerTerrain)
        {
            float terrainHeightMax = DaggerfallUnity.Instance.TerrainSampler.MaxTerrainHeight * daggerTerrain.TerrainScale;
            Vector3 ret = new Vector3(loc.terrainX * TERRAIN_SIZE_MULTI, daggerTerrain.MapData.averageHeight * terrainHeightMax + loc.heightOffset, loc.terrainY * TERRAIN_SIZE_MULTI);
            // Put type 2 instances at sea level
            if (loc.type == 2)
            {
                float oceanElevation = DaggerfallUnity.Instance.TerrainSampler.OceanElevation * daggerTerrain.TerrainScale;
                ret.y = oceanElevation - daggerTerrain.gameObject.transform.position.y;
            }
            return ret;
        }

        void InstantiatePrefab(string prefabName, LocationPrefab locationPrefab, LocationInstance loc, DaggerfallTerrain daggerTerrain)
        {
            // If it's the first time loading this prefab, load the non-dynamic objects into a template
            GameObject prefabObject;
            if (!prefabTemplates.TryGetValue(prefabName, out prefabObject))
            {
                prefabObject = new GameObject($"{prefabName}_Template");
                prefabObject.SetActive(false);
                Transform templateTransform = prefabObject.GetComponent<Transform>();
                templateTransform.parent = transform; // Put them under this mod for Hierarchy organization

                ModelCombiner combiner = new ModelCombiner();

                foreach (LocationObject obj in locationPrefab.obj)
                {
                    if (IsDynamicObject(obj))
                        continue;

                    GameObject go = LocationHelper.LoadStaticObject(
                        obj.type,
                        obj.name,
                        templateTransform,
                        obj.pos,
                        obj.rot,
                        obj.scale,
                        loc.locationID,
                        obj.objectID,
                        combiner
                        );

                    if (go != null)
                    {
                        if (go.GetComponent<DaggerfallBillboard>())
                        {
                            float tempY = go.transform.position.y;
                            go.GetComponent<DaggerfallBillboard>().AlignToBase();
                            go.transform.position = new Vector3(go.transform.position.x, tempY + ((go.transform.position.y - tempY) * go.transform.localScale.y), go.transform.position.z);
                        }

                        if (!go.GetComponent<DaggerfallLoot>())
                            go.isStatic = true;
                    }
                }

                if (combiner.VertexCount > 0)
                {
                    combiner.Apply();
                    GameObjectHelper.CreateCombinedMeshGameObject(combiner, $"{prefabName}_CombinedModels", templateTransform, makeStatic: true);
                }

                prefabTemplates.Add(prefabName, prefabObject);
            }
                        
            Vector3 terrainOffset = GetLocationPosition(loc, daggerTerrain);

            GameObject instance = Instantiate(prefabObject, new Vector3(), Quaternion.identity, daggerTerrain.gameObject.transform);
            instance.transform.localPosition = terrainOffset;
            instance.transform.localRotation = loc.rot;
            instance.name = prefabName;

            LocationData data = instance.AddComponent<LocationData>();
            data.Location = loc;
            data.Prefab = locationPrefab;

            // Now that we have the LocationData, add it to "pending instances" if needed
            if(loc.type == 2 && type2InstancePendingTerrains.TryGetValue(loc.locationID, out List<Vector2Int> pendingTerrains))
            {
                foreach (Vector2Int terrainCoord in pendingTerrains)
                {
                    if(!pendingType2Locations.TryGetValue(terrainCoord, out List<LocationData> pendingLocations))
                    {
                        pendingLocations = new List<LocationData>();
                        pendingType2Locations.Add(terrainCoord, pendingLocations);
                    }

                    pendingLocations.Add(data);
                }
            }

            instance.SetActive(true);

            if (!sceneLoading)
            {
                InstantiateInstanceDynamicObjects(instance, loc, locationPrefab);
            }
        }

        Dictionary<string, Mod> GetModGlobalFiles()
        {
            Dictionary<string, Mod> modGlobalFiles = new Dictionary<string, Mod>();

            foreach (Mod mod in ModManager.Instance.Mods)
            {
                if (!mod.Enabled)
                    continue;

                if (mod.AssetBundle && mod.AssetBundle.GetAllAssetNames().Length > 0)
                {
                    string dummyFilePath = mod.AssetBundle.GetAllAssetNames()[0];
                    string modFolderPrefix = dummyFilePath.Substring(17);
                    modFolderPrefix = dummyFilePath.Substring(0, 17 + modFolderPrefix.IndexOf('/'));

                    string globalFolder = modFolderPrefix + "/locations";

                    foreach (string filename in mod.AssetBundle.GetAllAssetNames())
                    {
                        string directoryName = Path.GetDirectoryName(filename).Replace('\\', '/');
                        if (!string.Equals(directoryName, globalFolder, System.StringComparison.InvariantCultureIgnoreCase))
                            continue;

                        if (!filename.EndsWith(".txt", StringComparison.InvariantCultureIgnoreCase) && !filename.EndsWith(".csv", System.StringComparison.InvariantCultureIgnoreCase))
                            continue;

                        string file = Path.GetFileName(filename).ToLower();
                        modGlobalFiles[file] = mod;
                    }
                }
#if UNITY_EDITOR
                else if (mod.IsVirtual && mod.ModInfo.Files.Count > 0)
                {
                    string dummyFilePath = mod.ModInfo.Files[0];
                    string modFolderPrefix = dummyFilePath.Substring(17);
                    modFolderPrefix = dummyFilePath.Substring(0, 17 + modFolderPrefix.IndexOf('/'));

                    string globalFolder = modFolderPrefix + "/Locations";

                    foreach (string filename in mod.ModInfo.Files)
                    {
                        string directoryName = Path.GetDirectoryName(filename).Replace('\\', '/');
                        if (!string.Equals(directoryName, globalFolder, System.StringComparison.InvariantCultureIgnoreCase))
                            continue;

                        if (!filename.EndsWith(".txt", StringComparison.InvariantCultureIgnoreCase) && !filename.EndsWith(".csv", System.StringComparison.InvariantCultureIgnoreCase))
                            continue;

                        string file = Path.GetFileName(filename).ToLower();
                        modGlobalFiles[file] = mod;
                    }
                }
#endif
            }

            string looseLocationFolder = Path.Combine(Application.dataPath, LocationHelper.locationInstanceFolder);
            if (Directory.Exists(looseLocationFolder))
            {
                foreach (string filename in Directory.GetFiles(looseLocationFolder)
                    .Where(file => file.EndsWith(".txt", System.StringComparison.InvariantCultureIgnoreCase) || file.EndsWith(".csv", System.StringComparison.InvariantCultureIgnoreCase))
                    .Select(file => Path.GetFileName(file).ToLower()))
                {
                    modGlobalFiles[filename] = null;
                }
            }

            return modGlobalFiles;
        }

        void CacheGlobalInstances()
        {
            Dictionary<string, Mod> modGlobalFiles = GetModGlobalFiles();

            foreach (var kvp in modGlobalFiles)
            {
                string filename = kvp.Key;
                Mod mod = kvp.Value;

                if (mod == null)
                {
                    string looseLocationFolder = Path.Combine(Application.dataPath, LocationHelper.locationInstanceFolder);
                    string looseFileLocation = Path.Combine(looseLocationFolder, filename);

                    foreach (LocationInstance instance in LocationHelper.LoadLocationInstance(looseFileLocation))
                    {
                        Vector2Int location = new Vector2Int(instance.worldX, instance.worldY);
                        List<LocationInstance> instances;
                        if (!worldPixelInstances.TryGetValue(location, out instances))
                        {
                            instances = new List<LocationInstance>();
                            worldPixelInstances.Add(location, instances);
                        }

                        instances.Add(instance);
                    }
                }
                else
                {
                    foreach (LocationInstance instance in LocationHelper.LoadLocationInstance(mod, filename))
                    {
                        Vector2Int location = new Vector2Int(instance.worldX, instance.worldY);
                        List<LocationInstance> instances;
                        if (!worldPixelInstances.TryGetValue(location, out instances))
                        {
                            instances = new List<LocationInstance>();
                            worldPixelInstances.Add(location, instances);
                        }

                        instances.Add(instance);
                    }
                }
            }
        }

        void CacheRegionInstances(int regionIndex)
        {
            CacheRegionFileNames(regionIndex);

            if (visitedRegions.Contains(regionIndex))
                return;

            Dictionary<string, Mod> regionFiles = modRegionFiles[regionIndex];
            foreach(var kvp in regionFiles)
            {
                string filename = kvp.Key;
                Mod mod = kvp.Value;

                if (mod == null)
                {
                    string looseLocationRegionFolder = Path.Combine(Application.dataPath, LocationHelper.locationInstanceFolder, regionIndex.ToString());
                    string looseFileLocation = Path.Combine(looseLocationRegionFolder, filename);

                    foreach(LocationInstance instance in LocationHelper.LoadLocationInstance(looseFileLocation))
                    {
                        Vector2Int location = new Vector2Int(instance.worldX, instance.worldY);
                        List<LocationInstance> instances;
                        if(!worldPixelInstances.TryGetValue(location, out instances))
                        {
                            instances = new List<LocationInstance>();
                            worldPixelInstances.Add(location, instances);
                        }

                        instances.Add(instance);
                    }
                }
                else
                {
                    foreach (LocationInstance instance in LocationHelper.LoadLocationInstance(mod, filename))
                    {
                        Vector2Int location = new Vector2Int(instance.worldX, instance.worldY);
                        List<LocationInstance> instances;
                        if (!worldPixelInstances.TryGetValue(location, out instances))
                        {
                            instances = new List<LocationInstance>();
                            worldPixelInstances.Add(location, instances);
                        }

                        instances.Add(instance);
                    }
                }
            }

            visitedRegions.Add(regionIndex);
        }

        void CacheRegionFileNames(int regionIndex)
        {
            if (!modRegionFiles.ContainsKey(regionIndex))
            {
                Dictionary<string, Mod> regionFiles = new Dictionary<string, Mod>();
                modRegionFiles.Add(regionIndex, regionFiles);

                string regionName = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetRegionName(regionIndex);

                foreach (Mod mod in ModManager.Instance.Mods)
                {
                    if (!mod.Enabled)
                        continue;

                    if (mod.AssetBundle && mod.AssetBundle.GetAllAssetNames().Length > 0)
                    {
                        string dummyFilePath = mod.AssetBundle.GetAllAssetNames()[0];
                        string modFolderPrefix = dummyFilePath.Substring(17);
                        modFolderPrefix = dummyFilePath.Substring(0, 17 + modFolderPrefix.IndexOf('/'));

                        string regionIndexFolder = modFolderPrefix + "/locations/" + regionIndex.ToString();                        
                        string regionNameFolder = modFolderPrefix + "/locations/" + regionName;


                        foreach (string filename in mod.AssetBundle.GetAllAssetNames()
                            .Where(file => (file.StartsWith(regionIndexFolder, StringComparison.InvariantCultureIgnoreCase) || file.StartsWith(regionNameFolder, StringComparison.InvariantCultureIgnoreCase))
                                && (file.EndsWith(".txt", StringComparison.InvariantCultureIgnoreCase) || file.EndsWith(".csv", System.StringComparison.InvariantCultureIgnoreCase)))
                            .Select(file => Path.GetFileName(file).ToLower()))
                        {
                            regionFiles[filename] = mod;
                        }
                    }
#if UNITY_EDITOR
                    else if (mod.IsVirtual && mod.ModInfo.Files.Count > 0)
                    {
                        string dummyFilePath = mod.ModInfo.Files[0];
                        string modFolderPrefix = dummyFilePath.Substring(17);
                        modFolderPrefix = dummyFilePath.Substring(0, 17 + modFolderPrefix.IndexOf('/'));

                        string regionIndexFolder = modFolderPrefix + "/Locations/" + regionIndex.ToString();
                        string regionNameFolder = modFolderPrefix + "/Locations/" + regionName;

                        foreach (string filename in mod.ModInfo.Files
                            .Where(file => (file.StartsWith(regionIndexFolder, System.StringComparison.InvariantCultureIgnoreCase) || file.StartsWith(regionNameFolder, System.StringComparison.InvariantCultureIgnoreCase))
                                && (file.EndsWith(".txt", System.StringComparison.InvariantCultureIgnoreCase) || file.EndsWith(".csv", System.StringComparison.InvariantCultureIgnoreCase)))
                            .Select(file => Path.GetFileName(file).ToLower()))
                        {
                            regionFiles[filename] = mod;
                        }
                    }
#endif                   
                }

                string looseLocationFolder = Path.Combine(Application.dataPath, LocationHelper.locationInstanceFolder);
                string looseLocationRegionIndexFolder = Path.Combine(looseLocationFolder, regionIndex.ToString());
                string looseLocationRegionNameFolder = Path.Combine(looseLocationFolder, regionName);
                if(Directory.Exists(looseLocationFolder) )
                {
                    if (Directory.Exists(looseLocationRegionIndexFolder))
                    {
                        foreach (string filename in Directory.GetFiles(looseLocationRegionIndexFolder)
                            .Where(file => file.EndsWith(".txt", System.StringComparison.InvariantCultureIgnoreCase) || file.EndsWith(".csv", System.StringComparison.InvariantCultureIgnoreCase))
                            .Select(file => Path.GetFileName(file).ToLower()))
                        {
                            regionFiles[filename] = null;
                        }
                    }

                    if (Directory.Exists(looseLocationRegionNameFolder))
                    {
                        foreach (string filename in Directory.GetFiles(looseLocationRegionNameFolder)
                            .Where(file => file.EndsWith(".txt", System.StringComparison.InvariantCultureIgnoreCase) || file.EndsWith(".csv", System.StringComparison.InvariantCultureIgnoreCase))
                            .Select(file => Path.GetFileName(file).ToLower()))
                        {
                            regionFiles[filename] = null;
                        }
                    }
                }
            }
        }

        void AddLocation(DaggerfallTerrain daggerTerrain, TerrainData terrainData)
        {
            Debug.Log($"Promoting terrain {daggerTerrain.MapPixelX}, {daggerTerrain.MapPixelY}");

            Vector2Int worldLocation = new Vector2Int(daggerTerrain.MapPixelX, daggerTerrain.MapPixelY);
            loadedTerrain[worldLocation] = new WeakReference<DaggerfallTerrain>(daggerTerrain);

            var regionIndex = GetRegionIndex(daggerTerrain);
            if(regionIndex != -1)
            {
                CacheRegionInstances(regionIndex);
            }            

            // Spawn the terrain's instances
            if (worldPixelInstances.TryGetValue(worldLocation, out List<LocationInstance> locationInstances))
            {
                foreach (LocationInstance loc in locationInstances)
                {
                    if (daggerTerrain.MapData.hasLocation)
                    {
                        if (loc.type == 0)
                        {
                            Debug.LogWarning("Location Already Present " + daggerTerrain.MapPixelX + " : " + daggerTerrain.MapPixelY);
                            continue;
                        }
                    }

                    if ((daggerTerrain.MapData.mapRegionIndex == 31 ||
                        daggerTerrain.MapData.mapRegionIndex == 3 ||
                        daggerTerrain.MapData.mapRegionIndex == 29 ||
                        daggerTerrain.MapData.mapRegionIndex == 28 ||
                        daggerTerrain.MapData.mapRegionIndex == 30) && daggerTerrain.MapData.worldHeight <= 2)
                    {
                        if (loc.type == 0)
                        {
                            Debug.LogWarning("Location is in Ocean " + daggerTerrain.MapPixelX + " : " + daggerTerrain.MapPixelY);
                            continue;
                        }
                    }

                    LocationPrefab locationPrefab = GetPrefabInfo(loc.prefab);
                    if (locationPrefab == null)
                        continue;

                    // Treating odd dimensions as ceiled-to-even
                    int halfWidth = (locationPrefab.width + 1) / 2;
                    int halfHeight = (locationPrefab.height + 1) / 2;

                    if (loc.type == 0)
                    {
                        if (loc.terrainX + halfWidth > 128
                            || loc.terrainY + halfHeight > 128
                            || loc.terrainX - halfWidth < 0
                            || loc.terrainY - halfHeight < 0)
                        {
                            Debug.LogWarning("Invalid Location at " + daggerTerrain.MapPixelX + " : " + daggerTerrain.MapPixelY + " : The locationpreset exist outside the terrain");
                            continue;
                        }
                    }

                    if (loc.type == 2)
                    {
                        // We find and adjust the type 2 instance position here
                        // So that terrain can be flattened in consequence
                        // If the current tile has no coast and adjacent terrain are not loaded,
                        // then we don't care about flattening, since it means the instance
                        // is gonna be on water at the edge of this tile anyway
                        if (FindNearestCoast(loc, daggerTerrain, out Vector2Int coastTileCoord))
                        {
                            loc.terrainX = coastTileCoord.x;
                            loc.terrainY = coastTileCoord.y;
                        }
                    }

                    //Smooth the terrain
                    int count = 0;
                    float tmpAverageHeight = 0;

                    // Type 1 instances can overlap beyond terrain boundaries
                    // Estimate height using only the part in the current terrain tile for now
                    int minX = Math.Max(loc.terrainX - halfWidth, 0);
                    int minY = Math.Max(loc.terrainY - halfHeight, 0);
                    int maxX = Math.Min(loc.terrainX + halfWidth, 128);
                    int maxY = Math.Min(loc.terrainY + halfHeight, 128);
                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            tmpAverageHeight += daggerTerrain.MapData.heightmapSamples[y, x];
                            count++;
                        }
                    }

                    daggerTerrain.MapData.averageHeight = tmpAverageHeight /= count;

                    if (loc.type == 0 || loc.type == 2)
                    {
                        daggerTerrain.MapData.locationRect = new Rect(minX, minY, maxX - minX, maxY - minY);

                        for (int y = 1; y < 127; y++)
                        {
                            for (int x = 1; x < 127; x++)
                            {
                                // Don't flatten heightmap samples touching water tiles
                                if (daggerTerrain.MapData.tilemapSamples[x, y] == 0
                                    || daggerTerrain.MapData.tilemapSamples[x + 1, y] == 0
                                    || daggerTerrain.MapData.tilemapSamples[x, y + 1] == 0
                                    || daggerTerrain.MapData.tilemapSamples[x + 1, y + 1] == 0)
                                {
                                    continue;
                                }

                                daggerTerrain.MapData.heightmapSamples[y, x] = Mathf.Lerp(daggerTerrain.MapData.heightmapSamples[y, x], daggerTerrain.MapData.averageHeight, 1 / (GetDistanceFromRect(daggerTerrain.MapData.locationRect, new Vector2(x, y)) + 1));
                            }
                        }
                    }

                    terrainData.SetHeights(0, 0, daggerTerrain.MapData.heightmapSamples);

                    InstantiatePrefab(loc.prefab, locationPrefab, loc, daggerTerrain);
                }
            }

            // Check for pending instances waiting on this terrain
            if(pendingType2Locations.TryGetValue(worldLocation, out List<LocationData> pendingLocations))
            {
                for(int i = 0; i < pendingLocations.Count; ++i)
                {
                    LocationData pendingLoc = pendingLocations[i];

                    if(pendingLoc == null)
                    {
                        // We got no info left on this instance
                        continue;
                    }

                    if(!type2InstancePendingTerrains.TryGetValue(pendingLoc.Location.locationID, out List<Vector2Int> pendingTerrains))
                    {
                        // Invalid locations?
                        continue;
                    }

                    // Removes the instance from all "pending terrains"
                    void ClearPendingInstance()
                    {
                        foreach(Vector2Int pendingTerrainCoord in pendingTerrains)
                        {
                            if (pendingTerrainCoord == worldLocation)
                                continue;

                            if(pendingType2Locations.TryGetValue(pendingTerrainCoord, out List<LocationData> pendingTerrainPendingInstances))
                            {
                                pendingTerrainPendingInstances.Remove(pendingLoc);
                                if (pendingTerrainPendingInstances.Count == 0)
                                    pendingType2Locations.Remove(pendingTerrainCoord);
                            }
                        }

                        type2InstancePendingTerrains.Remove(pendingLoc.Location.locationID);
                    }

                    if(!TryGetTerrain(pendingLoc.WorldX, pendingLoc.WorldY, out DaggerfallTerrain pendingLocTerrain))
                    {
                        // Terrain the location was on has expired
                        ClearPendingInstance();
                        continue;
                    }

                    if(FindNearestCoast(pendingLoc.Location, pendingLocTerrain, out Vector2Int coastCoord))
                    {
                        pendingLoc.Location.terrainX = coastCoord.x;
                        pendingLoc.Location.terrainY = coastCoord.y;
                        pendingLoc.gameObject.transform.localPosition = GetLocationPosition(pendingLoc.Location, pendingLocTerrain);

                        // Instance is not pending anymore
                        ClearPendingInstance();
                        continue;
                    }

                    // Remove this terrain from the location's pending terrains
                    pendingTerrains.Remove(worldLocation);
                }

                pendingType2Locations.Remove(worldLocation);
            }
        }

        private float GetDistanceFromRect(Rect rect, Vector2 point)
        {
            float squared_dist = 0.0f;

            if (point.x > rect.xMax)
                squared_dist += (point.x - rect.xMax) * (point.x - rect.xMax);
            else if (point.x < rect.xMin)
                squared_dist += (rect.xMin - point.x) * (rect.xMin - point.x);

            if (point.y > rect.yMax)
                squared_dist += (point.y - rect.yMax) * (point.y - rect.yMax);
            else if (point.y < rect.yMin)
                squared_dist += (rect.yMin - point.y) * (rect.yMin - point.y);

            return Mathf.Sqrt(squared_dist);
        }

        int GetRegionIndex(DaggerfallTerrain daggerfallTerrain)
        {
            if (daggerfallTerrain.MapData.mapRegionIndex != -1)
                return daggerfallTerrain.MapData.mapRegionIndex;

            int region = daggerfallTerrain.MapData.worldPolitic & 0x7F;
            // Region 64 is an "all water" terrain tile, according to UESP
            if(region < 0 || region >= DaggerfallUnity.Instance.ContentReader.MapFileReader.RegionCount || region == 64)
            {
                if(region != 64)
                    Debug.LogWarning($"Invalid region found at map location [{daggerfallTerrain.MapPixelX}, {daggerfallTerrain.MapPixelY}]");
                return -1;
            }

            return region;
        }

        bool IsDynamicObject(LocationObject obj)
        {
            return obj.type == 2;
        }

        bool FindNearestCoast(LocationInstance loc, DaggerfallTerrain daggerTerrain, out Vector2Int tileCoord)
        {
            byte GetTerrainSample(DaggerfallTerrain terrain, int x, int y)
            {
                return terrain.MapData.tilemapSamples[x, y];
            }

            byte GetSample(int x, int y)
            {
                return GetTerrainSample(daggerTerrain, x, y);
            }

            byte initial = GetSample(loc.terrainX, loc.terrainY);

            // If we start in water
            if(initial == 0)
            {
                // Find non-water in any direction
                for(int i = 1;;++i)
                {
                    bool anyValid = false;

                    // North
                    if(loc.terrainY + i < 128)
                    {
                        if(GetSample(loc.terrainX, loc.terrainY + i) != 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY + i - 1);
                            return true;
                        }
                        anyValid = true;
                    }

                    // East
                    if (loc.terrainX + i < 128)
                    {
                        if (GetSample(loc.terrainX + i, loc.terrainY) != 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX + i - 1, loc.terrainY);
                            return true;
                        }
                        anyValid = true;
                    }

                    // South
                    if (loc.terrainY - i >= 0)
                    {
                        if (GetSample(loc.terrainX, loc.terrainY - i) != 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY - i + 1);
                            return true;
                        }
                        anyValid = true;
                    }

                    // West
                    if (loc.terrainX - i >= 0)
                    {
                        if (GetSample(loc.terrainX - i, loc.terrainY) != 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX - i + 1, loc.terrainY);
                            return true;
                        }
                        anyValid = true;
                    }

                    if (!anyValid)
                        break;
                }
                                
                // Look the edges of adjacent terrain
                if (GetNorthNeighbor(daggerTerrain, out DaggerfallTerrain northNeighbor))
                {
                    if(GetTerrainSample(northNeighbor, loc.terrainX, 0) != 0)
                    {
                        tileCoord = new Vector2Int(loc.terrainX, 127);
                        return true;
                    }
                }

                if (GetEastNeighbor(daggerTerrain, out DaggerfallTerrain eastNeighbor))
                {
                    if (GetTerrainSample(eastNeighbor, 0, loc.terrainY) != 0)
                    {
                        tileCoord = new Vector2Int(127, loc.terrainY);
                        return true;
                    }
                }

                if (GetSouthNeighbor(daggerTerrain, out DaggerfallTerrain southNeighbor))
                {
                    if (GetTerrainSample(southNeighbor, loc.terrainX, 127) != 0)
                    {
                        tileCoord = new Vector2Int(loc.terrainX, 0);
                        return true;
                    }
                }

                if (GetEastNeighbor(daggerTerrain, out DaggerfallTerrain westNeighbor))
                {
                    if (GetTerrainSample(westNeighbor, 127, loc.terrainY) != 0)
                    {
                        tileCoord = new Vector2Int(0, loc.terrainY);
                        return true;
                    }
                }

                List<Vector2Int> pendingTerrain = new List<Vector2Int>();
                if(northNeighbor == null && loc.worldY != 0)
                {
                    pendingTerrain.Add(new Vector2Int(loc.worldX, loc.worldY - 1));
                }

                if (eastNeighbor == null && loc.worldX != 1000)
                {
                    pendingTerrain.Add(new Vector2Int(loc.worldX + 1, loc.worldY));
                }

                if (southNeighbor == null && loc.worldY != 500)
                {
                    pendingTerrain.Add(new Vector2Int(loc.worldX, loc.worldY + 1));
                }

                if (westNeighbor == null && loc.worldX != 0)
                {
                    pendingTerrain.Add(new Vector2Int(loc.worldX - 1, loc.worldY));
                }

                if (pendingTerrain.Count != 0)
                {
                    Debug.Log($"Location {loc.locationID} waiting for pending terrain");

                    type2InstancePendingTerrains[loc.locationID] = pendingTerrain;
                }
            }
            else
            {
                // Find water in any direction
                for (int i = 1; ; ++i)
                {
                    bool anyValid = false;

                    // North
                    if (loc.terrainY + i < 128)
                    {
                        if (GetSample(loc.terrainX, loc.terrainY + i) == 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY + i);
                            return true;
                        }
                        anyValid = true;
                    }

                    // East
                    if (loc.terrainX + i < 128)
                    {
                        if (GetSample(loc.terrainX + i, loc.terrainY) == 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX + i, loc.terrainY);
                            return true;
                        }
                        anyValid = true;
                    }

                    // South
                    if (loc.terrainY - i >= 0)
                    {
                        if (GetSample(loc.terrainX, loc.terrainY - i) == 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY - i);
                            return true;
                        }
                        anyValid = true;
                    }

                    // West
                    if (loc.terrainX - i >= 0)
                    {
                        if (GetSample(loc.terrainX - i, loc.terrainY) == 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX - i, loc.terrainY);
                            return true;
                        }
                        anyValid = true;
                    }

                    if (!anyValid)
                        break;
                }
            }

            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY);
            return false;
        }

        bool GetNorthNeighbor(DaggerfallTerrain daggerTerrain, out DaggerfallTerrain northNeighbor)
        {
            if (daggerTerrain.TopNeighbour != null)
            {
                northNeighbor = daggerTerrain.TopNeighbour.GetComponent<DaggerfallTerrain>();
                return true;
            }

            if(daggerTerrain.MapPixelY == 0)
            {
                northNeighbor = null;
                return false;
            }

            return TryGetTerrain(daggerTerrain.MapPixelX, daggerTerrain.MapPixelY - 1, out northNeighbor);
        }

        bool GetEastNeighbor(DaggerfallTerrain daggerTerrain, out DaggerfallTerrain eastNeighbor)
        {
            if (daggerTerrain.RightNeighbour != null)
            {
                eastNeighbor = daggerTerrain.RightNeighbour.GetComponent<DaggerfallTerrain>();
                return true;
            }

            if (daggerTerrain.MapPixelX == 1000)
            {
                eastNeighbor = null;
                return false;
            }

            return TryGetTerrain(daggerTerrain.MapPixelX + 1, daggerTerrain.MapPixelY, out eastNeighbor);
        }

        bool GetSouthNeighbor(DaggerfallTerrain daggerTerrain, out DaggerfallTerrain southNeighbor)
        {
            if (daggerTerrain.BottomNeighbour != null)
            {
                southNeighbor = daggerTerrain.BottomNeighbour.GetComponent<DaggerfallTerrain>();
                return true;
            }

            if (daggerTerrain.MapPixelY == 500)
            {
                southNeighbor = null;
                return false;
            }

            return TryGetTerrain(daggerTerrain.MapPixelX, daggerTerrain.MapPixelY + 1, out southNeighbor);
        }

        bool GetWestNeighbor(DaggerfallTerrain daggerTerrain, out DaggerfallTerrain westNeighbor)
        {
            if (daggerTerrain.LeftNeighbour != null)
            {
                westNeighbor = daggerTerrain.LeftNeighbour.GetComponent<DaggerfallTerrain>();
                return true;
            }

            if (daggerTerrain.MapPixelX == 0)
            {
                westNeighbor = null;
                return false;
            }

            return TryGetTerrain(daggerTerrain.MapPixelX - 1, daggerTerrain.MapPixelY, out westNeighbor);
        }
    }
}
