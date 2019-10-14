﻿using System.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine;
namespace MPipeline
{
    [RequireComponent(typeof(Camera))]
    public unsafe sealed class VTDecalCamera : MonoBehaviour, IPipelineRunnable
    {
        public struct CameraState
        {
            public int cullingMask;
            public float3 position;
            public quaternion rotation;
            public float size;
            public float nearClipPlane;
            public float farClipPlane;
            public RenderTargetIdentifier albedoRT;
            public RenderTargetIdentifier normalRT;
            public RenderTargetIdentifier smoRT;
            public RenderTargetIdentifier heightRT;
            public int depthSlice;
        }
        [EasyButtons.Button]
        void SetAspect()
        {
            GetComponent<Camera>().aspect = 1;
        }
        private Camera cam;
        private RenderTargetIdentifier[] idfs;
        public NativeList<CameraState> renderingCommand { get; private set; }
        private void Awake()
        {
            cam = GetComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.aspect = 1;
            renderingCommand = new NativeList<CameraState>(10, Allocator.Persistent);
            idfs = new RenderTargetIdentifier[3];
            RenderPipeline.AddRunnableObject(GetInstanceID(), this);
        }

        private void OnDestroy()
        {
            cam = null;
            renderingCommand.Dispose();
        }

        public void PipelineUpdate(ref PipelineCommandData data)
        {
            if (renderingCommand.Length <= 0) return;
            CommandBuffer buffer = data.buffer;
            for (int i = 0; i < renderingCommand.Length; ++i)
            {
                ref CameraState orthoCam = ref renderingCommand[i];
                transform.position = orthoCam.position;
                transform.rotation = orthoCam.rotation;
                cam.orthographicSize = orthoCam.size;
                cam.nearClipPlane = orthoCam.nearClipPlane;
                cam.farClipPlane = orthoCam.farClipPlane;
                #region CAMERA_RENDERING
                ScriptableCullingParameters cullParam;
                data.context.SetupCameraProperties(cam);
                if (!cam.TryGetCullingParameters(out cullParam)) continue;
                cullParam.cullingMask = (uint)orthoCam.cullingMask;
                cullParam.cullingOptions = CullingOptions.ForceEvenIfCameraIsNotActive;
                CullingResults result = data.context.Cull(ref cullParam);
                FilteringSettings filter = new FilteringSettings
                {
                    layerMask = orthoCam.cullingMask,
                    renderingLayerMask = uint.MaxValue,
                    renderQueueRange = new RenderQueueRange(1000, 5000)
                };
                SortingSettings sort = new SortingSettings(cam)
                {
                    criteria = SortingCriteria.RenderQueue
                };
                DrawingSettings drawS = new DrawingSettings(new ShaderTagId("TerrainDecal"), sort)
                {
                    perObjectData = UnityEngine.Rendering.PerObjectData.None
                };
                DrawingSettings drawH = new DrawingSettings(new ShaderTagId("TerrainDisplacement"), sort)
                {
                    perObjectData = UnityEngine.Rendering.PerObjectData.None
                };
                ComputeShader copyShader = data.resources.shaders.texCopyShader;
                buffer.GetTemporaryRT(ShaderIDs._GPURPHeightMap, MTerrain.HEIGHT_RESOLUTION, MTerrain.HEIGHT_RESOLUTION, 16, FilterMode.Bilinear, MTerrain.HEIGHT_FORMAT);
                buffer.CopyTexture(orthoCam.heightRT, orthoCam.depthSlice, ShaderIDs._GPURPHeightMap, 0);
                buffer.SetGlobalTexture(ShaderIDs._VirtualHeightmap, orthoCam.heightRT);
                buffer.SetGlobalInt(ShaderIDs._OffsetIndex, orthoCam.depthSlice);
                var terrainData = MTerrain.current.terrainData;
                buffer.SetGlobalVector(ShaderIDs._HeightScaleOffset, float4(terrainData.heightScale, terrainData.heightOffset, 1, 1));
                buffer.GetTemporaryRT(RenderTargets.gbufferIndex[0], MTerrain.COLOR_RESOLUTION, MTerrain.COLOR_RESOLUTION, 16, FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1, false);
                buffer.GetTemporaryRT(RenderTargets.gbufferIndex[2], MTerrain.COLOR_RESOLUTION, MTerrain.COLOR_RESOLUTION, 0, FilterMode.Point, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear, 1, false);
                buffer.GetTemporaryRT(RenderTargets.gbufferIndex[1], MTerrain.COLOR_RESOLUTION, MTerrain.COLOR_RESOLUTION, 0, FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1, false);
                idfs[0] = RenderTargets.gbufferIndex[0];
                idfs[1] = RenderTargets.gbufferIndex[2];
                idfs[2] = RenderTargets.gbufferIndex[1];
                buffer.SetRenderTarget(colors: idfs, depth: idfs[0]);
                buffer.ClearRenderTarget(true, true, new Color(0,0,0,0));
                data.ExecuteCommandBuffer();
                data.context.DrawRenderers(result, ref drawS, ref filter);
                buffer.SetRenderTarget(color: ShaderIDs._GPURPHeightMap, depth:ShaderIDs._GPURPHeightMap);
                buffer.ClearRenderTarget(true, false, Color.black);
                data.ExecuteCommandBuffer();
                data.context.DrawRenderers(result, ref drawH, ref filter);
                buffer.SetComputeTextureParam(copyShader, 6, ShaderIDs._VirtualMainTex, orthoCam.albedoRT);
                buffer.SetComputeTextureParam(copyShader, 6, ShaderIDs._VirtualBumpMap, orthoCam.normalRT);
                buffer.SetComputeTextureParam(copyShader, 6, ShaderIDs._VirtualSMO, orthoCam.smoRT);
                buffer.SetComputeTextureParam(copyShader, 6, RenderTargets.gbufferIndex[0], RenderTargets.gbufferIndex[0]);
                buffer.SetComputeTextureParam(copyShader, 6, RenderTargets.gbufferIndex[1], RenderTargets.gbufferIndex[1]);
                buffer.SetComputeTextureParam(copyShader, 6, RenderTargets.gbufferIndex[2], RenderTargets.gbufferIndex[2]);
                buffer.SetComputeIntParam(copyShader, ShaderIDs._Count, orthoCam.depthSlice);
                int disp = MTerrain.COLOR_RESOLUTION / 8;
                buffer.DispatchCompute(copyShader, 6, disp, disp, 1);
                buffer.ReleaseTemporaryRT(RenderTargets.gbufferIndex[0]);
                buffer.ReleaseTemporaryRT(RenderTargets.gbufferIndex[1]);
                buffer.ReleaseTemporaryRT(RenderTargets.gbufferIndex[2]);
                buffer.CopyTexture(ShaderIDs._GPURPHeightMap, 0, orthoCam.heightRT, orthoCam.depthSlice);
                buffer.ReleaseTemporaryRT(ShaderIDs._GPURPHeightMap);
                data.ExecuteCommandBuffer();
                #endregion
            }
            data.context.Submit();
            renderingCommand.Clear();
        }
    }
}
