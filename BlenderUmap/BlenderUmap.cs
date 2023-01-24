﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlenderUmap;
using BlenderUmap.Extensions;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SkiaSharp;
using static CUE4Parse.UE4.Assets.Exports.Texture.EPixelFormat;

// ReSharper disable PositionalPropertyUsedProblem
namespace BlenderUmap {
    public static class Program {
        public static Config config;
        public static MyFileProvider provider;
        private static readonly long start = DateTimeOffset.Now.ToUnixTimeMilliseconds();
#if DEBUG
        private static readonly bool NoExport = false;
#else
        private static readonly bool NoExport = false;
#endif
        public static uint ThreadWorkCount = 0;

        public static void Main(string[] args) {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine("Logs", $"BlenderUmap-{DateTime.Now:yyyy-MM-dd}.log"))
                .WriteTo.Console()
                .CreateLogger();
#if !DEBUG
        try {
#endif
            var configFile = new FileInfo("config.json");
                if (!configFile.Exists) {
                    Log.Error("config.json not found");
                    return;
                }

                Log.Information("Reading config file {0}", configFile.FullName);

                using (var reader = configFile.OpenText()) {
                    config = new JsonSerializer().Deserialize<Config>(new JsonTextReader(reader));
                }

                var paksDir = config.PaksDirectory;
                if (!Directory.Exists(paksDir)) {
                    throw new MainException("Directory " + Path.GetFullPath(paksDir) + " not found.");
                }

                if (string.IsNullOrEmpty(config.ExportPackage)) {
                    throw new MainException("Please specify ExportPackage.");
                }

                ObjectTypeRegistry.RegisterEngine(typeof(USpotLightComponent).Assembly);

                provider = new MyFileProvider(paksDir, new VersionContainer(config.Game, optionOverrides: config.OptionsOverrides), config.EncryptionKeys, config.bDumpAssets, config.ObjectCacheSize);
                provider.LoadVirtualPaths();
                var newestUsmap = GetNewestUsmap(new DirectoryInfo("mappings"));
                if (newestUsmap != null) {
                    var usmap = new FileUsmapTypeMappingsProvider(newestUsmap.FullName);
                    usmap.Reload();
                    provider.MappingsContainer = usmap;
                    Log.Information("Loaded mappings from {0}", newestUsmap.FullName);
                }

                var pkg = ExportAndProduceProcessed(config.ExportPackage, new List<string>());
                if (pkg == null) Environment.Exit(1); // prevent addon from importing previously exported maps

                while (ThreadWorkCount > 0) {
                    Console.Write($"\rWaiting for {ThreadWorkCount} threads to exit...");
                    Thread.Sleep(1000);
                }
                Console.WriteLine();

                var file = new FileInfo("processed.json");
                Log.Information("Writing to {0}", file.FullName);
                using (var writer = file.CreateText()) {
                    var pkgName = provider.CompactFilePath(pkg.Name);
                    new JsonSerializer().Serialize(writer, pkgName);
                }

                Log.Information("All done in {0:F1} sec. In the Python script, replace the line with data_dir with this line below:\n\ndata_dir = r\"{1}\"", (DateTimeOffset.Now.ToUnixTimeMilliseconds() - start) / 1000.0F, Directory.GetCurrentDirectory());
            }
#if !DEBUG
        catch (Exception e) {
                if (e is MainException) {
                    Log.Information(e.Message);
                } else {
                    Log.Error(e, "An unexpected error has occurred, please report");
                }
                Environment.Exit(1);
            }
        }
#endif

        public static FileInfo GetNewestUsmap(DirectoryInfo directory) {
            FileInfo chosenFile = null;
            if (!directory.Exists) return null;
            var files = directory.GetFiles().OrderByDescending(f => f.LastWriteTime);
            foreach (var f in files) {
                if (f.Extension == ".usmap") {
                    chosenFile = f;
                    break;
                }
            }
            return chosenFile;
        }

        public static bool CheckIfHasLights(IPackage actorPackage, out List<ULightComponent> lightcomps) {
            lightcomps = new List<ULightComponent>();
            if (actorPackage == null) return false;
            foreach (var export in actorPackage.GetExports()) {
                if (export is ULightComponent lightComponent) {
                    lightcomps.Add(lightComponent);
                }
            }
            return lightcomps.Count > 0;
        }

        public static IPackage ExportAndProduceProcessed(string path, List<string> loadedLevels) {
            UObject obj = null;
            if (path.EndsWith(".replay")) {
                // throw new NotSupportedException("replays are not supported by this version of BlenderUmap.");
                return ReplayExporter.ExportAndProduceProcessed(path, provider);
            }

            if (provider.TryLoadPackage(path, out var pkg)) { // multiple exports with package name
                if (pkg is Package notioPackage) {
                    foreach (var export in notioPackage.ExportMap) {
                        if (export.ClassName == "World") {
                            obj = export.ExportObject.Value;
                        }
                    }
                }
            }

            if (obj == null) {
                if (path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    path = $"{path.SubstringBeforeLast(".")}.{path.SubstringAfterLast("/").SubstringBeforeLast(".")}";

                if (!provider.TryLoadObject(path, out obj)) {
                    Log.Warning("Object {0} not found", path);
                    return null;
                }
            }

            // if (obj.ExportType == "FortPlaysetItemDefinition") {
            //     return FortPlaysetItemDefinition.ExportAndProduceProcessed(obj, provider);
            // }

            if (obj is not UWorld world) {
                Log.Information("{0} is not a World, won't try to export", obj.GetPathName());
                return null;
            }
            loadedLevels.Add(provider.CompactFilePath(world.GetPathName()));
            var persistentLevel = world.PersistentLevel.Load<ULevel>();
            var comps = new JArray();
            var lights = new List<LightInfo2>();
            for (var index = 0; index < persistentLevel.Actors.Length; index++) {
                var actorLazy = persistentLevel.Actors[index];
                if (actorLazy == null || actorLazy.IsNull) continue;
                var actor = actorLazy.Load();
                if (actor.ExportType == "LODActor") continue;
                Log.Information("Loading {0}: {1}/{2} {3}", world.Name, index, persistentLevel.Actors.Length,
                    actorLazy);
                ProcessActor(actor, lights, comps, loadedLevels);

                if (index % 100 == 0) { // every 100th actor
                    GC.Collect();
                }
            }

            if (config.bExportBuildingFoundations) {
                foreach (var streamingLevelLazy in world.StreamingLevels) {
                    UObject streamingLevel = streamingLevelLazy.Load();
                    if (streamingLevel == null) continue;

                    var children = new JArray();
                    string text = streamingLevel.GetOrDefault<FSoftObjectPath>("WorldAsset").AssetPathName.Text;
                    if (loadedLevels.Contains(text))
                        continue;
                    var cpkg = ExportAndProduceProcessed(text.SubstringBeforeLast('.'), loadedLevels);
                    children.Add(cpkg != null ? provider.CompactFilePath(cpkg.Name) : null);

                    var transform = streamingLevel.GetOrDefault<FTransform>("LevelTransform", FTransform.Identity);

                    var comp = new JArray {
                        JValue.CreateNull(), // GUID
                        streamingLevel.Name,
                        JValue.CreateNull(), // mesh path
                        JValue.CreateNull(), // materials
                        JValue.CreateNull(), // texture data
                        Vector(transform.Translation), // location
                        Quat(transform.Rotation), // rotation
                        Vector(transform.Scale3D), // scale
                        children,
                        0 // Light index
                    };
                    comps.Add(comp);
                }
            }

            // var pkg = world.Owner;
            string pkgName = provider.CompactFilePath(obj.Owner.Name).SubstringAfter("/");
            var file = new FileInfo(Path.Combine(MyFileProvider.JSONS_FOLDER.ToString(), pkgName + ".processed.json"));
            file.Directory.Create();
            Log.Information("Writing to {0}", file.FullName);

            using var writer = file.CreateText();

            var file2 = new FileInfo(Path.Combine(MyFileProvider.JSONS_FOLDER.ToString(), pkgName + ".lights.processed.json"));
            file2.Directory.Create();

            using var writer2 = file2.CreateText();
#if DEBUG
            new JsonSerializer() { Formatting = Formatting.Indented }.Serialize(writer, comps);
            new JsonSerializer() { Formatting = Formatting.Indented }.Serialize(writer2, lights);
#else
            new JsonSerializer().Serialize(writer, comps);
            new JsonSerializer().Serialize(writer2, lights);
#endif

            return obj.Owner;
        }

        public static void ProcessStreamingGrid(FStructFallback grid, JArray children, List<string> loadedLevels) {
            var tasks = new List<Task>();
            var bagged = new ConcurrentBag<string>();
            if (grid.TryGetValue(out FStructFallback[] gridLevels, "GridLevels")) {
                foreach (var level in gridLevels) {
                    if (level.TryGetValue<FStructFallback[]>(out var levelCells, "LayerCells")) {
                        foreach (var levelCell in levelCells) {
                            if (levelCell.TryGetValue<UObject[]>(out var gridCells, "GridCells")) {
                                foreach (var gridCell in gridCells) {
                                    if (gridCell.TryGetValue<UObject>(out var levelStreaming, "LevelStreaming") && levelStreaming.TryGetValue(out FSoftObjectPath worldAsset, "WorldAsset")) {
                                        var text = worldAsset.ToString();
                                        if (text.SubstringAfterLast("/").StartsWith("HLOD"))
                                            continue;
                                        // GC.Collect();
                                        var childPackage = ExportAndProduceProcessed(text, loadedLevels);
                                        children.Add(childPackage != null ? provider.CompactFilePath(childPackage.Name) : null);
                                        // tasks.Add(Task.Run(() => {
                                        //     var childPackage = ExportAndProduceProcessed(text);
                                        //     // bagged.Add(childPackage != null ? provider.CompactFilePath(childPackage.Name) : null);
                                        // }));
                                    }
                                }
                            }
                        }
                    }
                }
            }
            // Task.WaitAll(tasks.ToArray());
            // foreach (var child in bagged) {
            //     children.Add(child != null ? provider.CompactFilePath(child) : null);
            // }
        }

        public static void ProcessActor(UObject actor, List<LightInfo2> lights, JArray comps, List<string> loadedLevels) {
            if (actor is ALight) {
                var lightcomp = actor.GetOrDefault<ULightComponent>("LightComponent", null);
                if (lightcomp is not null) {
                    // unused
                    var lloc = lightcomp.GetOrDefault<FVector>("RelativeLocation");
                    var lrot = lightcomp.GetOrDefault<FRotator>("RelativeRotation", new FRotator(-90,0,0)); // actor is ARectLight ? new FRotator(-90,0,0) :
                    var lscale = lightcomp.GetOrDefault<FVector>("RelativeScale3D", FVector.OneVector);

                    var lightInfo2 = new LightInfo2 {
                        Props = new []{ lightcomp } // TODO: Support InstanceComponents
                    };
                    lights.Add(lightInfo2);

                    var lcomp = new JArray {
                        JValue.CreateNull(), // GUID
                        actor.Name,
                        JValue.CreateNull(), // mesh path
                        JValue.CreateNull(), // materials
                        JValue.CreateNull(), // texture data
                        Vector(lloc), // location
                        Rotator(lrot), // rotation
                        Vector(lscale), // scale
                        JValue.CreateNull(),
                        -lights.Count // 0 -> no light -ve -> light no parent, +ve light with parent (BP actors only?) | so actual light index is abs(LightIndex)-1
                    };
                    comps.Add(lcomp);
                }

                return;
            }
            if (actor.TryGetValue(out UObject partition, "WorldPartition")
                && partition.TryGetValue(out UObject runtineHash, "RuntimeHash")
                && runtineHash.TryGetValue(out FStructFallback[] streamingGrids, "StreamingGrids")) {
                FStructFallback grid = null;
                foreach (var t in streamingGrids) {
                    if (t.TryGetValue(out FName name, "GridName")) {
                        if (!name.ToString().StartsWith("HLOD")) {
                            grid = t;
                            break;
                        }
                    }
                }
                if (grid == null) return;

                var childrenLevel = new JArray();
                ProcessStreamingGrid(grid, childrenLevel, loadedLevels);
                if (childrenLevel.Count == 0) return;

                // identifiers
                var streamComp = new JArray();
                comps.Add(streamComp);
                streamComp.Add(Guid.NewGuid().ToString().Replace("-", ""));
                streamComp.Add(actor.Name);
                streamComp.Add(null);
                streamComp.Add(null);
                streamComp.Add(null);
                streamComp.Add(Vector(grid.GetOrDefault<FVector>("Origin", FVector.ZeroVector)));
                streamComp.Add(Rotator(new FRotator()));
                streamComp.Add(Vector(FVector.OneVector));
                streamComp.Add(childrenLevel);
                streamComp.Add(0); // LightIndex
                return;
            }

            var staticMeshCompLazy = actor.GetOrDefault<FPackageIndex>("StaticMeshComponent", new FPackageIndex()); // /Script/Engine.StaticMeshActor:StaticMeshComponent or /Script/FortniteGame.BuildingSMActor:StaticMeshComponent
            if (staticMeshCompLazy.IsNull) return;


            // identifiers
            var comp = new JArray();
            comps.Add(comp);
            comp.Add(actor.TryGetValue<FGuid>(out var guid, "MyGuid") // /Script/FortniteGame.BuildingActor:MyGuid
                ? guid.ToString(EGuidFormats.Digits).ToLowerInvariant()
                : Guid.NewGuid().ToString().Replace("-", ""));
            comp.Add(actor.Name);

            // region mesh
            var staticMeshComp = staticMeshCompLazy?.Load();
            var mesh = staticMeshComp!.GetOrDefault<FPackageIndex>("StaticMesh"); // /Script/Engine.StaticMeshComponent:StaticMesh

            if (mesh == null || mesh.IsNull) { // read the actor class to find the mesh
                var actorBlueprint = actor.Class;

                    if (actorBlueprint is UBlueprintGeneratedClass) {
                        if (actorBlueprint.Owner != null)
                            foreach (var actorExp in actorBlueprint.Owner.GetExports()) {
                                if (actorExp.ExportType != "FortKillVolume_C" &&
                                    (mesh = actorExp.GetOrDefault<FPackageIndex>("StaticMesh")) != null) {
                                    break;
                                }
                            }
                        if (mesh == null) {
                            // look in parent struct if not found
                            var super = actorBlueprint.SuperStruct.Load<UBlueprintGeneratedClass>();
                            if (super != null)
                                foreach (var actorExp in super.Owner.GetExports()!) {
                                    if (actorExp.ExportType != "FortKillVolume_C" &&
                                        (mesh = actorExp.GetOrDefault<FPackageIndex>("StaticMesh")) != null) {
                                        break;
                                    }
                                }
                        }
                    }
            }
            // endregion

            var matsObj = new JObject(); // matpath: [4x[str]]
            var textureDataArr = new List<Dictionary<string, string>>();
            var materials = new List<Mat>();
            ExportMesh(mesh, materials);

            if (config.bReadMaterials /*&& actor is BuildingSMActor*/) {
                var material = actor.GetOrDefault<FPackageIndex>("BaseMaterial"); // /Script/FortniteGame.BuildingSMActor:BaseMaterial
                var overrideMaterials = staticMeshComp.GetOrDefault<List<FPackageIndex>>("OverrideMaterials"); // /Script/Engine.MeshComponent:OverrideMaterials

                var textureDatas = actor.GetProps<FPackageIndex>("TextureData");
                for (var texIndex = 0; texIndex < textureDatas.Length; texIndex++) {
                    var textureDataIdx = textureDatas[texIndex];
                    // /Script/FortniteGame.BuildingSMActor:TextureData
                    var td = textureDataIdx?.Load();

                    if (td != null) {
                        var textures = new Dictionary<string, string>();
                        AddToArray(textures, td.GetOrDefault<FPackageIndex>("Diffuse"), texIndex == 0 ? "Diffuse" : $"Diffuse_Texture_{texIndex+1}"); //
                        AddToArray(textures, td.GetOrDefault<FPackageIndex>("Normal"),  texIndex == 0 ? "Normals" : $"Normals_Texture_{texIndex+1}"); //
                        AddToArray(textures, td.GetOrDefault<FPackageIndex>("Specular"), texIndex == 0 ? "SpecularMasks" : $"SpecularMasks_{texIndex+1}");
                        textureDataArr.Add(textures);

                        var overrideMaterial = td.GetOrDefault<FPackageIndex>("OverrideMaterial");
                        if (overrideMaterial is { IsNull: false }) {
                            material = overrideMaterial;
                        }
                    }
                    else {
                        textureDataArr.Add(new Dictionary<string, string>());
                    }
                }

                for (int i = 0; i < materials.Count; i++) {
                    var mat = materials[i];
                    if (overrideMaterials != null && i < overrideMaterials.Count && overrideMaterials[i] is {IsNull: false}) {
                        // var matIndex = overrideMaterials != null && i < overrideMaterials.Count && overrideMaterials[i] is {IsNull: false} ? overrideMaterials[i] : material;
                        mat.Material = overrideMaterials[i].ResolvedObject; //matIndex.ResolvedObject;
                    }
                    mat.PopulateTextures();

                    mat.AddToObj(matsObj, textureDataArr);
                }
            }

            // region additional worlds
            var children = new JArray();
            var additionalWorlds = actor.GetOrDefault<List<FSoftObjectPath>>("AdditionalWorlds"); // /Script/FortniteGame.BuildingFoundation:AdditionalWorlds

            if (config.bExportBuildingFoundations && additionalWorlds != null) {
                foreach (var additionalWorld in additionalWorlds) {
                    var text = additionalWorld.AssetPathName.Text;
                    GC.Collect();
                    var childPackage = ExportAndProduceProcessed(text, loadedLevels);
                    children.Add(childPackage != null ? provider.CompactFilePath(childPackage.Name) : null);
                }
            }
            // endregion

            var loc = staticMeshComp.GetOrDefault<FVector>("RelativeLocation");
            var rot = staticMeshComp.GetOrDefault<FRotator>("RelativeRotation", FRotator.ZeroRotator);
            var scale = staticMeshComp.GetOrDefault<FVector>("RelativeScale3D", FVector.OneVector);
            comp.Add(PackageIndexToDirPath(mesh));
            comp.Add(matsObj);
            comp.Add(JArray.FromObject(textureDataArr));
            comp.Add(Vector(loc)); // /Script/Engine.SceneComponent:RelativeLocation
            comp.Add(Rotator(rot)); // /Script/Engine.SceneComponent:RelativeRotation
            comp.Add(Vector(scale)); // /Script/Engine.SceneComponent:RelativeScale3D
            comp.Add(children);

            int LightIndex = 0;
            if (CheckIfHasLights(actor.Class.Outer?.Owner, out var lightinfo)) {
                var infor = new LightInfo2() {
                    Props = lightinfo.ToArray()
                };
                lights.Add(infor);
                LightIndex = lights.Count;
            }
            comp.Add(LightIndex);
        }

        public static void AddToArray(Dictionary<string, string> matDict, FPackageIndex index, string ParamName) {
            if (index != null) {
                ExportTexture(index);
                matDict[ParamName] = PackageIndexToDirPath(index);
            } else {
                // matDict.Add(JValue.CreateNull());
            }
        }

        private static void ExportTexture(FPackageIndex index) {
            if (NoExport) return;
            var obj = index.Load();
            if (obj is not UTexture2D texture) {
                return;
            }

            char[] fourCC = config.bExportToDDSWhenPossible ? GetDDSFourCC(texture) : null;
            var output = new FileInfo(Path.Combine(GetExportDir(texture).ToString(), texture.Name + (fourCC != null ? ".dds" : ".png")));

            if (output.Exists) {
                Log.Debug("Texture already exists, skipping: {0}", output.FullName);
            } else {
                if (fourCC != null) {
                    throw new NotImplementedException("DDS export is not implemented");
                }

                ThreadPool.QueueUserWorkItem(_ => {
                    Interlocked.Increment(ref ThreadWorkCount);
                    Log.Information("Saving texture to {0}", output.FullName);
                    // CUE4Parse only reads the first FTexturePlatformData and drops the rest
                    try {
                        var firstMip = texture.GetFirstMip(); // Modify this if you want lower res textures
                        using var image = texture.Decode(firstMip);
                        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                        using var stream = output.OpenWrite();
                        data.SaveTo(stream);
                        Interlocked.Decrement(ref ThreadWorkCount);
                    }
                    catch (IOException) { Interlocked.Decrement(ref ThreadWorkCount); } // two threads trying to write same texture
                    catch (Exception e) { Log.Warning(e, "Failed to save texture"); Interlocked.Decrement(ref ThreadWorkCount); }
                });
            }
        }

        public static void ExportMesh(FPackageIndex mesh, List<Mat> materials) {
            var meshExport = mesh?.Load<UStaticMesh>();
            if (meshExport == null) return;
            var output = new FileInfo(Path.Combine(GetExportDir(meshExport).ToString(), meshExport.Name + ".pskx"));

            if (!output.Exists && !NoExport) {
                ThreadPool.QueueUserWorkItem(_ => {
                    if (!output.Exists) {
                        try {
                            Interlocked.Increment(ref ThreadWorkCount);
                            Log.Information("Saving mesh to {0}", output.FullName);
                            var exporter = new MeshExporter(meshExport, new ExporterOptions(), false);
                            if (exporter.MeshLods.Count == 0) {
                                Log.Warning("Mesh '{0}' has no LODs", meshExport.Name);
                                Interlocked.Decrement(ref ThreadWorkCount);
                                return;
                            }
                            var stream = output.OpenWrite();
                            stream.Write(exporter.MeshLods.First().FileData);
                            stream.Close();
                            Interlocked.Decrement(ref ThreadWorkCount);
                        }
                        catch (IOException) { Interlocked.Decrement(ref ThreadWorkCount); } // two threads trying to write same mesh
                        catch (Exception e) { Log.Warning(e, "Failed to save mesh"); Interlocked.Decrement(ref ThreadWorkCount); }
                    }
                });
            }

            if (config.bReadMaterials) {
                var matObjs = meshExport.Materials;
                if (matObjs != null) {
                    foreach (var material in matObjs) {
                        materials.Add(new Mat(material));
                    }
                }
            }
        }

        public static DirectoryInfo GetExportDir(UObject exportObj) => GetExportDir(exportObj.Owner);

        public static DirectoryInfo GetExportDir(IPackage package) {
            string pkgPath = provider.CompactFilePath(package.Name);
            pkgPath = pkgPath.SubstringBeforeLast('.');

            if (pkgPath.StartsWith("/")) {
                pkgPath = pkgPath[1..];
            }

            var outputDir = new FileInfo(pkgPath).Directory;
            // string pkgName = pkgPath.SubstringAfterLast('/');

            // what's this for?
            // if (exportObj.Name != pkgName) {
            //     outputDir = new DirectoryInfo(Path.Combine(outputDir.ToString(), pkgName));
            // }

            outputDir.Create();
            return outputDir;
        }

        public static string PackageIndexToDirPath(UObject obj) {
            string pkgPath = provider.CompactFilePath(obj.Owner.Name);
            pkgPath = pkgPath.SubstringBeforeLast('.');
            var objectName = obj.Name;
            return pkgPath.SubstringAfterLast('/') == objectName ? pkgPath : pkgPath + '/' + objectName;
        }

        public static string PackageIndexToDirPath(ResolvedObject obj) {
            if (obj == null) return null;

            string pkgPath = provider.CompactFilePath(obj.Package.Name);
            pkgPath = pkgPath.SubstringBeforeLast('.');
            var objectName = obj.Name.Text;
            return String.Compare(pkgPath.SubstringAfterLast('/'), objectName, StringComparison.OrdinalIgnoreCase) == 0 ? pkgPath : pkgPath + '/' + objectName;
        }

        public static string PackageIndexToDirPath(FPackageIndex obj) {
            return PackageIndexToDirPath(obj?.ResolvedObject);
        }

        public static JArray Vector(FVector vector) => new() {vector.X, vector.Y, vector.Z};
        public static JArray Rotator(FRotator rotator) => new() {rotator.Pitch, rotator.Yaw, rotator.Roll};
        public static JArray Quat(FQuat quat) => new() {quat.X, quat.Y, quat.Z, quat.W};

        private static char[] GetDDSFourCC(UTexture2D texture) => (texture.Format switch {
            PF_DXT1 => "DXT1",
            PF_DXT3 => "DXT3",
            PF_DXT5 => "DXT5",
            PF_BC4 => "ATI1",
            PF_BC5 => "ATI2",
            _ => null
        })?.ToCharArray();

        public static T[] GetProps<T>(this IPropertyHolder obj, string name) {
            var collected = new List<FPropertyTag>();
            var maxIndex = -1;
            foreach (var prop in obj.Properties) {
                if (prop.Name.Text == name) {
                    collected.Add(prop);
                    maxIndex = Math.Max(maxIndex, prop.ArrayIndex);
                }
            }

            var array = new T[maxIndex + 1];
            foreach (var prop in collected) {
                array[prop.ArrayIndex] = (T) prop.Tag.GetValue(typeof(T));
            }

            return array;
        }

        private static T GetAnyValueOrDefault<T>(this Dictionary<string, T> dict, string[] keys) {
            foreach (var key in keys) {
                foreach (var kvp in dict) {
                    if (kvp.Key.Equals(key))
                        return kvp.Value;
                }
            }
            return default;
        }

        public class Mat {
            public ResolvedObject Material;
            public string ShaderName = "None";

            private readonly Dictionary<string, FPackageIndex> _textureParameterValues = new();
            private readonly Dictionary<string, float> _scalarParameterValues = new();
            private readonly Dictionary<string, string> _vectorParameterValues = new(); // hex


            public Mat(ResolvedObject material) {
                Material = material;
            }

            public void PopulateTextures() {
                PopulateTextures(Material?.Load());
            }

            private void PopulateTextures(UObject obj) {
                if (obj is not UMaterialInterface material) {
                    return;
                }

                if (obj is UMaterial uMaterial) {
                    ShaderName = obj.Name;
                }

                #region PossiblyOldFormat
                foreach (var propertyTag in material.Properties) {
                    if (propertyTag.Tag == null) return;

                    var text = propertyTag.Tag.GetValue(typeof(FExpressionInput));
                    if (text is FExpressionInput materialInput) {
                        var expression = obj.Owner!.GetExportOrNull(materialInput.ExpressionName.ToString());
                        if (expression != null && expression.TryGetValue(out FPackageIndex texture, "Texture")) {
                            if (!_textureParameterValues.ContainsKey(propertyTag.Name.ToString())) {
                                _textureParameterValues[propertyTag.Name.ToString()] = texture;
                            }
                        }
                    }
                }

                string[] TEXTURES = new [] {"MaterialExpressionTextureObjectParameter", "MaterialExpressionTextureSampleParameter2D"};
                string[] VECTORS = new [] {"MaterialExpressionVectorParameter"};
                string[] BOOLS = new [] {"MaterialExpressionStaticBoolParameter"};
                string[] SCALERS = new [] {"MaterialExpressionScalarParameter"};

                if (material.TryGetValue(out UObject[] exports, "Expressions")) {
                    foreach (var export in exports) {
                        if (export != null && export.TryGetValue(out FName name, "ParameterName") && !name.IsNone) {
                            if (TEXTURES.Contains(export.ExportType)) {
                                if (export.TryGetValue(out FPackageIndex parameterValue, "Texture"))
                                    if (!_textureParameterValues.ContainsKey(name.Text))
                                        _textureParameterValues[name.Text] = parameterValue;
                            }
                            if (VECTORS.Contains(export.ExportType)) {
                                if (export.TryGetValue(out FLinearColor color, "DefaultValue"))
                                    if (!_vectorParameterValues.ContainsKey(name.Text))
                                        _vectorParameterValues[name.Text] = color.ToSRGB().ToString();
                            }
                            if (BOOLS.Contains(export.ExportType)) {
                                if (export.TryGetValue(out bool val, "DefaultValue"))
                                    if (!_scalarParameterValues.ContainsKey(name.Text))
                                        _scalarParameterValues[name.Text] = val ? 1 : 0;
                            }if (SCALERS.Contains(export.ExportType)) {
                                if (export.TryGetValue(out float val, "DefaultValue"))
                                    if (!_scalarParameterValues.ContainsKey(name.Text))
                                        _scalarParameterValues[name.Text] = val;
                            }
                        }
                    }
                }
                // for some materials we still don't have the textures
                #endregion

                #region Texture
                var textureParameterValues =
                    material.GetOrDefault<List<FTextureParameterValue>>("TextureParameterValues");
                if (textureParameterValues != null) {
                    foreach (var textureParameterValue in textureParameterValues) {
                        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                        if (textureParameterValue.ParameterInfo == null) continue;
                        var name = textureParameterValue.ParameterInfo.Name;
                        if (!name.IsNone) {
                            var parameterValue = textureParameterValue.ParameterValue;
                            if (!_textureParameterValues.ContainsKey(name.Text)) {
                                _textureParameterValues[name.Text] = parameterValue;
                            }
                        }
                    }
                }
                #endregion

                #region Scaler
                var scalerParameterValues =
                    material.GetOrDefault<List<FScalarParameterValue>>("ScalarParameterValues", new List<FScalarParameterValue>());
                foreach (var scalerParameterValue in scalerParameterValues) {
                    if (!_scalarParameterValues.ContainsKey(scalerParameterValue.Name))
                        _scalarParameterValues[scalerParameterValue.Name] = scalerParameterValue.ParameterValue;
                }
                #endregion

                #region Vector
                var vectorParameterValues = material.GetOrDefault<List<FVectorParameterValue>>("VectorParameterValues", new List<FVectorParameterValue>());
                foreach (var vectorParameterValue in vectorParameterValues) {
                    if (!_vectorParameterValues.ContainsKey(vectorParameterValue.Name)) {
                        if (vectorParameterValue.ParameterValue != null)
                            _vectorParameterValues[vectorParameterValue.Name] = vectorParameterValue.ParameterValue.Value.ToSRGB().ToString();
                    }
                }
                #endregion

                if (material is UMaterialInstance mi) {
                    var staticParameters = mi.StaticParameters;
                    if (staticParameters != null)
                        foreach (var switchParameter in staticParameters.StaticSwitchParameters) {
                            if (!_scalarParameterValues.ContainsKey(switchParameter.Name))
                                _scalarParameterValues[switchParameter.Name] = switchParameter.Value ? 1 : 0;
                        }
                    if (mi.Parent != null) {
                        PopulateTextures(mi.Parent);
                    }
                }
            }

            public void AddToObj(JObject obj, List<Dictionary<string, string>> overrides) {
                var mergedOverrides = new Dictionary<string, string>();
                foreach (var oOverride in overrides) {
                    foreach (var k in oOverride) {
                        mergedOverrides[k.Key] = k.Value;
                    }
                }

                if (Material == null) {
                    obj.Add(GetHashCode().ToString("x"), null);
                    return;
                }

                var textArray = new JObject();
                foreach (var text in _textureParameterValues) {
                    if (mergedOverrides.ContainsKey(text.Key)) {
                        textArray[text.Key] = mergedOverrides[text.Key];
                        continue;
                    }
                    var index = text.Value;
                    if (index is { IsNull: false }) {
                        ExportTexture(index);
                        textArray[text.Key] = PackageIndexToDirPath(index);
                    }
                }

                foreach (var item in mergedOverrides) {
                    if (!textArray.ContainsKey(item.Key)) {
                        textArray[item.Key] = item.Value;
                    }
                }

                var scalerArray = JObject.FromObject(_scalarParameterValues);
                var vectorArray = JObject.FromObject(_vectorParameterValues);

                if (!obj.ContainsKey(PackageIndexToDirPath(Material))) {
                    var combinedParams = new JObject();

                    combinedParams.Add(nameof(ShaderName), ShaderName);
                    combinedParams.Add("TextureParams", textArray);
                    combinedParams.Add("ScalerParams", scalerArray);
                    combinedParams.Add("VectorParams", vectorArray);

                    obj.Add(PackageIndexToDirPath(Material), combinedParams);
                }
            }
        }
    }

    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    [SuppressMessage("ReSharper", "ConvertToConstant.Global")]
    [SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global")]
    public class Config {
        public string PaksDirectory = "C:\\Program Files\\Epic Games\\Fortnite\\FortniteGame\\Content\\Paks";
        [JsonProperty("UEVersion")]
        public EGame Game = EGame.GAME_UE4_LATEST;
        public Dictionary<string, bool> OptionsOverrides = new Dictionary<string, bool>();
        public List<EncryptionKey> EncryptionKeys = new();
        public bool bDumpAssets = false;
        public int ObjectCacheSize = 100;
        public bool bReadMaterials = true;
        public bool bExportToDDSWhenPossible = true;
        public bool bExportBuildingFoundations = true;
        public string ExportPackage;
        public TextureMapping Textures = new();
    }

    public class TextureMapping {
        public TextureMap UV1 = new() {
            Diffuse = new[] {"Trunk_BaseColor", "Diffuse", "DiffuseTexture", "Base_Color_Tex", "Tex_Color"},
            Normal = new[] {"Trunk_Normal", "Normals", "Normal", "Base_Normal_Tex", "Tex_Normal"},
            Specular = new[] {"Trunk_Specular", "SpecularMasks"},
            Emission = new[] {"EmissiveTexture"},
            MaskTexture = new[] {"MaskTexture"}
        };
        public TextureMap UV2 = new() {
            Diffuse = new[] {"Diffuse_Texture_3"},
            Normal = new[] {"Normals_Texture_3"},
            Specular = new[] {"SpecularMasks_3"},
            Emission = new[] {"EmissiveTexture_3"},
            MaskTexture = new[] {"MaskTexture_3"}
        };
        public TextureMap UV3 = new() {
            Diffuse = new[] {"Diffuse_Texture_4"},
            Normal = new[] {"Normals_Texture_4"},
            Specular = new[] {"SpecularMasks_4"},
            Emission = new[] {"EmissiveTexture_4"},
            MaskTexture = new[] {"MaskTexture_4"}
            };
        public TextureMap UV4 = new() {
            Diffuse = new[] {"Diffuse_Texture_2"},
            Normal = new[] {"Normals_Texture_2"},
            Specular = new[] {"SpecularMasks_2"},
            Emission = new[] {"EmissiveTexture_2"},
            MaskTexture = new[] {"MaskTexture_2"}
            };
    }

    public class TextureMap {
        public string[] Diffuse;
        public string[] Normal;
        public string[] Specular;
        public string[] Emission;
        public string[] MaskTexture;
    }

    public class LightInfo2 {
        public ULightComponent[] Props;
    }

    public class MainException : Exception {
        public MainException(string message) : base(message) { }
    }
}