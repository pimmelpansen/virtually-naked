using SharpDX.Direct3D11;
using System.Collections.Generic;
using System.IO;
using System.Linq;

class MaterialSetDumper {
	private static MultiMaterialSettings DumpMaterialSet(ImportSettings settings, Device device, ShaderCache shaderCache, ContentFileLocator fileLocator, DsonObjectLocator objectLocator, ImporterPathManager pathManager, Figure figure, MaterialSetImportConfiguration baseConfiguration, MaterialSetImportConfiguration configuration, TextureProcessor sharedTextureProcessor) {
		DirectoryInfo figureDirectory = pathManager.GetDestDirForFigure(figure.Name);
		DirectoryInfo materialsSetsDirectory = figureDirectory.Subdirectory("material-sets");
		DirectoryInfo materialSetDirectory = materialsSetsDirectory.Subdirectory(configuration.name);
		FileInfo materialSettingsFileInfo = materialSetDirectory.File("material-settings.dat");
		FileInfo faceTransparenciesFileInfo = materialSetDirectory.File("face-transparencies.array");
		if (materialSettingsFileInfo.Exists && faceTransparenciesFileInfo.Exists) {
			return Persistance.Load<MultiMaterialSettings>(UnpackedArchiveFile.Make(materialSettingsFileInfo));
		}
		
		var aggregator = new DsonMaterialAggregator(fileLocator, objectLocator);
		IEnumerable<string> dufPaths = Enumerable.Concat(baseConfiguration.materialsDufPaths, configuration.materialsDufPaths);
		foreach (string path in dufPaths) {
			DsonTypes.DsonDocument doc = objectLocator.LocateRoot(path);
			aggregator.IncludeDuf(doc.Root);
		}
		
		TextureProcessor localTextureProcessor;
		if (sharedTextureProcessor == null) {
			localTextureProcessor = new TextureProcessor(device, shaderCache, materialSetDirectory, settings.CompressTextures);
		} else {
			localTextureProcessor = null;
		}
	
		var textureProcessor = sharedTextureProcessor ?? localTextureProcessor;

		var faceTransparencyProcessor = new FaceTransparencyProcessor(device, shaderCache, pathManager, figure);

		IMaterialImporter materialImporter;
		if (figure.Name.EndsWith("-hair")) {
			materialImporter = new HairMaterialImporter(figure, textureProcessor, faceTransparencyProcessor);
		} else {
			materialImporter = new UberMaterialImporter(figure, textureProcessor, faceTransparencyProcessor);
		}
		
		string[] surfaceNames = figure.Geometry.SurfaceNames;
		Dictionary<string, int> surfaceNameToIdx = Enumerable.Range(0, surfaceNames.Length)
			.ToDictionary(idx => surfaceNames[idx], idx => idx);

		var perMaterialSettings = Enumerable.Range(0, figure.Geometry.SurfaceCount)
			.Select(surfaceIdx => {
				string surfaceName = figure.Geometry.SurfaceNames[surfaceIdx];
				var bag = aggregator.GetBag(surfaceName);
				var materialSettings = materialImporter.Import(surfaceIdx, bag);
				return materialSettings;
			})
			.ToArray();

		var variantCategories = configuration.variantCategories
			.Select(variantCategoryConf => {
				int[] surfaceIdxs = variantCategoryConf.surfaces
					.Select(surfaceName => surfaceNameToIdx[surfaceName])
					.ToArray();

				var variants = variantCategoryConf.variants
					.Select(variantConf => {
						var variantAggregator = aggregator.Branch();
						foreach (string path in variantConf.materialsDufPaths) {
							DsonTypes.DsonDocument doc = objectLocator.LocateRoot(path);
							variantAggregator.IncludeDuf(doc.Root);
						}

						var settingsBySurface = variantCategoryConf.surfaces
							.Select(surfaceName => {
								int surfaceIdx = surfaceNameToIdx[surfaceName];
								var bag = variantAggregator.GetBag(surfaceName);
								var materialSettings = materialImporter.Import(surfaceIdx, bag);
								return materialSettings;
							})
							.ToArray();

						return new MultiMaterialSettings.Variant(variantConf.name, settingsBySurface);
					})
					.ToArray();

				return new MultiMaterialSettings.VariantCategory(variantCategoryConf.name, surfaceIdxs, variants);
			})
			.ToArray();

		var multiMaterialSettings = new MultiMaterialSettings(perMaterialSettings, variantCategories);
		
		materialSetDirectory.CreateWithParents();

		textureProcessor.RegisterAction(() => {
			Persistance.Save(materialSettingsFileInfo, multiMaterialSettings);
		});

		localTextureProcessor?.ImportAll();

		var faceTranparencies = faceTransparencyProcessor.FaceTransparencies;
		faceTransparenciesFileInfo.WriteArray(faceTranparencies);

		faceTransparencyProcessor.Dispose();

		return multiMaterialSettings;
	}

	public static void DumpMaterialSetAndScattering(ImportSettings settings, Device device, ShaderCache shaderCache, ContentFileLocator fileLocator, DsonObjectLocator objectLocator, ImporterPathManager pathManager, Figure figure,
		MaterialSetImportConfiguration baseConfiguration, MaterialSetImportConfiguration configuration, TextureProcessor sharedTextureProcessor) {
		var materialSettings = DumpMaterialSet(settings, device, shaderCache, fileLocator, objectLocator, pathManager, figure, baseConfiguration, configuration, sharedTextureProcessor);
		ScatteringDumper.Dump(pathManager, figure, materialSettings.PerMaterialSettings, configuration.name);
	}

	public static void DumpAllForFigure(ImportSettings settings, Device device, ShaderCache shaderCache, ContentFileLocator fileLocator, DsonObjectLocator objectLocator, ImporterPathManager pathManager, Figure figure, TextureProcessorSharer textureProcessorSharer) {
		MaterialSetImportConfiguration[] configurations = MaterialSetImportConfiguration.Load(pathManager, figure.Name);

		var baseConf = configurations.Single(conf => conf.name == "Base");
		
		var surfaceProperties = SurfacePropertiesJson.Load(pathManager, figure);
		TextureProcessor sharedTextureProcessor = surfaceProperties.ShareTextures != null ?
			textureProcessorSharer.GetSharedProcessor(surfaceProperties.ShareTextures) : null;

		foreach (var conf in configurations) {
			if (conf == baseConf) {
				continue;
			}

			if (!settings.ShouldImportMaterialSet(figure.Name, conf.name)) {
				continue;
			}

			DumpMaterialSetAndScattering(settings, device, shaderCache, fileLocator, objectLocator, pathManager, figure, baseConf, conf, sharedTextureProcessor);
		}
	}
}
