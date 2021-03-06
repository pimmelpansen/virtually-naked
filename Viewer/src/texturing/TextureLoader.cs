using SharpDX;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;

public class TextureLoader : IDisposable {
	public enum DefaultMode {
		Standard,
		Bump
	}

	private readonly Device device;
	private readonly TextureCache textureCache;
	private readonly IArchiveDirectory texturesDirectory;
	private readonly ShaderResourceView defaultStandardTexture;
	private readonly ShaderResourceView defaultBumpTexture;
	private readonly Dictionary<string, SharedTexture> cache = new Dictionary<string, SharedTexture>();

	public TextureLoader(Device device, TextureCache textureCache, IArchiveDirectory texturesDirectory) {
		this.device = device;
		this.textureCache = textureCache;
		this.texturesDirectory = texturesDirectory;
		this.defaultStandardTexture = MakeMonochromeTexture(device, Vector4.One);
		this.defaultBumpTexture = MakeMonochromeTexture(device, new Vector4(0.5f, 0.5f, 1, 1));
	}

	public void Dispose() {
		foreach (var resource in cache.Values) {
			resource.Dispose();
		}
		defaultStandardTexture.Dispose();
		defaultBumpTexture.Dispose();
	}
	
	private static ShaderResourceView MakeMonochromeTexture(Device device, Vector4 value) {
		using (var whiteTexture = MonochromaticTextures.Make(device, value)) {
			return new ShaderResourceView(device, whiteTexture);
		}
	}
	
	public ShaderResourceView Load(string name, DefaultMode defaultMode) {
		if (name == null) {
			if (defaultMode == DefaultMode.Bump) {
				return defaultBumpTexture;
			} else {
				return defaultStandardTexture;
			}
		}

		if (!cache.TryGetValue(name, out var textureView)) {
			var path = (name + ".dds").Split('/');
			var imageFile = texturesDirectory.File(path);
			textureView = textureCache.Get(imageFile);
			cache.Add(name, textureView);
		}

		return textureView;
	}
}
