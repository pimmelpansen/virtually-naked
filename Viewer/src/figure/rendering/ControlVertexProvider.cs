using SharpDX.Direct3D11;
using System;
using Device = SharpDX.Direct3D11.Device;
using SharpDX;
using System.Collections.Generic;
using System.Linq;

public class ControlVertexProvider : IDisposable {
	public static readonly int ControlVertex_SizeInBytes = Vector3.SizeInBytes + OcclusionInfo.PackedSizeInBytes;

	private static IOccluder LoadOccluder(Device device, ShaderCache shaderCache, ChannelSystem channelSystem, bool isMainFigure, IArchiveDirectory unmorphedOcclusionDirectory, IArchiveDirectory occlusionDirectory) {
		if (isMainFigure) {
			IArchiveFile occluderParametersFile = occlusionDirectory.File("occluder-parameters.dat");
			if (occluderParametersFile == null) {
				throw new InvalidOperationException("expected main figure to have occlusion system");
			}
			
			var occluderParameters = Persistance.Load<OccluderParameters>(occluderParametersFile);
			OcclusionInfo[] unmorphedOcclusionInfos = OcclusionInfo.UnpackArray(unmorphedOcclusionDirectory.File("occlusion-infos.array").ReadArray<uint>());
			var occluder = new DeformableOccluder(device, shaderCache, channelSystem, unmorphedOcclusionInfos, occluderParameters);
			return occluder;
		} else {
			OcclusionInfo[] figureOcclusionInfos = OcclusionInfo.UnpackArray(occlusionDirectory.File("occlusion-infos.array").ReadArray<uint>());
			OcclusionInfo[] parentOcclusionInfos = OcclusionInfo.UnpackArray(occlusionDirectory.File("parent-occlusion-infos.array").ReadArray<uint>());
			var occluder = new StaticOccluder(device, figureOcclusionInfos, parentOcclusionInfos);
			return occluder;
		}
	}

	public static ControlVertexProvider Load(Device device, ShaderCache shaderCache, FigureDefinition definition, FigureModel model) {
		var shaperParameters = Persistance.Load<ShaperParameters>(definition.Directory.File("shaper-parameters.dat"));
								
		var unmorphedOcclusionDirectory = definition.Directory.Subdirectory("occlusion");
		var occlusionDirectory = model.Shape.Directory ?? unmorphedOcclusionDirectory;
		
		bool isMainFigure = definition.ChannelSystem.Parent == null;

		var occluder = LoadOccluder(device, shaderCache, definition.ChannelSystem, isMainFigure, unmorphedOcclusionDirectory, occlusionDirectory);

		var provider = new ControlVertexProvider(
			device, shaderCache,
			definition,
			shaperParameters,
			occluder,
			model.IsVisible);

		model.ShapeChanged += (oldShape, newShape) => {
			var newOcclusionDirectory = model.Shape.Directory ?? unmorphedOcclusionDirectory;
			var newOccluder = LoadOccluder(device, shaderCache, definition.ChannelSystem, isMainFigure, unmorphedOcclusionDirectory, newOcclusionDirectory);
			provider.SetOccluder(newOccluder);
		};

		model.IsVisibleChanged += (oldVisibile, newVisible) => {
			provider.SetIsVisible(newVisible);
		};

		return provider;
	}
	
	private readonly FigureDefinition definition;
	private IOccluder occluder;
	private readonly GpuShaper shaper;
	private bool isVisible;
	
	private readonly int vertexCount;

	private readonly InOutStructuredBufferManager<ControlVertexInfo> controlVertexInfosBufferManager;

	private const int BackingArrayCount = 2;
	private readonly StagingStructuredBufferManager<ControlVertexInfo> controlVertexInfoStagingBufferManager;

	public ControlVertexProvider(Device device, ShaderCache shaderCache,
		FigureDefinition definition,
		ShaperParameters shaperParameters,
		IOccluder occluder,
		bool isVisible) {
		this.definition = definition;
		this.shaper = new GpuShaper(device, shaderCache, definition, shaperParameters);
		this.occluder = occluder;
		this.isVisible = isVisible;
		
		this.vertexCount = shaperParameters.InitialPositions.Length;
		
		controlVertexInfosBufferManager = new InOutStructuredBufferManager<ControlVertexInfo>(device, vertexCount);

		if (definition.ChannelSystem.Parent == null) {
			this.controlVertexInfoStagingBufferManager = new StagingStructuredBufferManager<ControlVertexInfo>(device, vertexCount, BackingArrayCount);
		}
	}
	
	public void Dispose() {
		foreach (var child in children) {
			child.OccluderChanged -= RegisterChildOccluders;
		}

		shaper.Dispose();
		occluder.Dispose();
		controlVertexInfosBufferManager.Dispose();

		if (controlVertexInfoStagingBufferManager != null) {
			controlVertexInfoStagingBufferManager.Dispose();
		}
	}

	private event Action OccluderChanged;

	private void SetOccluder(IOccluder newOccluder) {
		occluder.Dispose();
		occluder = newOccluder;
		OccluderChanged?.Invoke();

		if (children != null) {
			RegisterChildOccluders();
		}
	}
	
	private void SetIsVisible(bool newIsVisible) {
		isVisible = newIsVisible;
		OccluderChanged?.Invoke();
	}

	public int VertexCount => vertexCount;

	public ShaderResourceView OcclusionInfos => occluder.OcclusionInfosView;
	public ShaderResourceView ControlVertexInfosView => controlVertexInfosBufferManager.InView;
	private List<ControlVertexProvider> children = new List<ControlVertexProvider>();

	public void RegisterChildren(List<ControlVertexProvider> children) {
		var oldChildren = this.children;
		foreach (var oldChild in oldChildren) {
			oldChild.OccluderChanged -= RegisterChildOccluders;
		}

		this.children = children;

		foreach (var child in children) {
			child.OccluderChanged += RegisterChildOccluders;
		}
		RegisterChildOccluders();
	}

	private void RegisterChildOccluders() {
		var childOccluders = children
			.Where(child => child.isVisible)
			.Select(child => child.occluder)
			.ToList();
		occluder.RegisterChildOccluders(childOccluders);
	}

	public ChannelOutputs UpdateFrame(DeviceContext context, ChannelOutputs parentOutputs, ChannelInputs inputs) {
		var channelOutputs = definition.ChannelSystem.Evaluate(parentOutputs, inputs);
		var boneTransforms = definition.BoneSystem.GetBoneTransforms(channelOutputs);
		occluder.SetValues(context, channelOutputs);
		shaper.SetValues(context, channelOutputs, boneTransforms);
		return channelOutputs;
	}

	public void UpdateVertexPositionsAndGetDeltas(DeviceContext context, UnorderedAccessView deltasOutView) {
		occluder.CalculateOcclusion(context);
		shaper.CalculatePositionsAndDeltas(
			context,
			controlVertexInfosBufferManager.OutView,
			occluder.OcclusionInfosView,
			deltasOutView);
		controlVertexInfoStagingBufferManager.CopyToStagingBuffer(context, controlVertexInfosBufferManager.Buffer);
	}

	public void UpdateVertexPositions(DeviceContext context, ShaderResourceView parentDeltasView) {
		occluder.CalculateOcclusion(context);
		shaper.CalculatePositions(
			context,
			controlVertexInfosBufferManager.OutView,
			occluder.OcclusionInfosView,
			parentDeltasView);
	}
	
	private volatile ControlVertexInfo[] previousFramePosedVertices;

	public void ReadbackPosedControlVertices(DeviceContext context) {
		if (controlVertexInfoStagingBufferManager != null) {
			previousFramePosedVertices = controlVertexInfoStagingBufferManager.FillArrayFromStagingBuffer(context);
		}
	}

	public ControlVertexInfo[] GetPreviousFrameResults(DeviceContext context) {
		return previousFramePosedVertices;
	}
}
