using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using System;
using System.Collections.Generic;

namespace UnityEngine.Experimental.ScriptableRenderLoop
{
    [ExecuteInEditMode]
    public class FptlLighting : ScriptableRenderLoop
    {
#if UNITY_EDITOR
        [UnityEditor.MenuItem("Renderloop/CreateRenderLoopFPTL")]
        static void CreateRenderLoopFPTL()
        {
            var instance = ScriptableObject.CreateInstance<FptlLighting>();
            UnityEditor.AssetDatabase.CreateAsset(instance, "Assets/renderloopfptl.asset");
            //AssetDatabase.CreateAsset(instance, "Assets/ScriptableRenderLoop/fptl/renderloopfptl.asset");
        }

#endif

        [SerializeField]
        ShadowSettings m_ShadowSettings = ShadowSettings.Default;
        ShadowRenderPass m_ShadowPass;

        [SerializeField]
        TextureSettings m_TextureSettings = TextureSettings.Default;

        public Shader deferredShader;
        public Shader deferredReflectionShader;
        public Shader finalPassShader;

        public ComputeShader buildScreenAABBShader;
        public ComputeShader buildPerTileLightListShader;     // FPTL

        public ComputeShader buildPerVoxelLightListShader;    // clustered

        private Material m_DeferredMaterial;
        private Material m_DeferredReflectionMaterial;
        private static int s_GBufferAlbedo;
        private static int s_GBufferSpecRough;
        private static int s_GBufferNormal;
        private static int s_GBufferEmission;
        private static int s_GBufferZ;
        private static int s_CameraTarget;
        private static int s_CameraDepthTexture;

        private static int s_GenAABBKernel;
        private static int s_GenListPerTileKernel;
        private static int s_GenListPerVoxelKernel;
        private static int s_ClearVoxelAtomicKernel;
        private static ComputeBuffer s_LightDataBuffer;
        private static ComputeBuffer s_ConvexBoundsBuffer;
        private static ComputeBuffer s_AABBBoundsBuffer;
        private static ComputeBuffer s_LightList;
        private static ComputeBuffer s_DirLightList;

        // clustered light list specific buffers and data begin
        public bool enableClustered = false;
        const bool k_UseDepthBuffer = true;//      // only has an impact when EnableClustered is true (requires a depth-prepass)
        const bool disableFptlWhenClustered = false;    // still useful on opaques
        const int k_Log2NumClusters = 6;     // accepted range is from 0 to 6. NumClusters is 1<<g_iLog2NumClusters
        const float k_ClustLogBase = 1.02f;     // each slice 2% bigger than the previous
        float m_ClustScale;
        private static ComputeBuffer s_PerVoxelLightLists;
        private static ComputeBuffer s_PerVoxelOffset;
        private static ComputeBuffer s_PerTileLogBaseTweak;
        private static ComputeBuffer s_GlobalLightListAtomic;
        // clustered light list specific buffers and data end

        private static int s_WidthOnRecord;
        private static int s_HeightOnRecord;

        Matrix4x4[] m_MatWorldToShadow = new Matrix4x4[k_MaxLights * k_MaxShadowmapPerLights];
        Vector4[] m_DirShadowSplitSpheres = new Vector4[k_MaxDirectionalSplit];
        Vector4[] m_Shadow3X3PCFTerms = new Vector4[4];

        public const int MaxNumLights = 1024;
        public const int MaxNumDirLights = 2;
        public const float FltMax = 3.402823466e+38F;

        const int k_MaxLights = 10;
        const int k_MaxShadowmapPerLights = 6;
        const int k_MaxDirectionalSplit = 4;
        // Directional lights become spotlights at a far distance. This is the distance we pull back to set the spotlight origin.
        const float k_DirectionalLightPullbackDistance = 10000.0f;

        [NonSerialized]
        private int m_WarnedTooManyLights = 0;

        private TextureCache2D m_CookieTexArray;
        private TextureCacheCubemap m_CubeCookieTexArray;
        private TextureCacheCubemap m_CubeReflTexArray;

        private SkyboxHelper m_SkyboxHelper;

        private Material m_BlitMaterial;

        void OnEnable()
        {
            Rebuild();
        }

        void OnValidate()
        {
            Rebuild();
        }

        void ClearComputeBuffers()
        {
            if (s_AABBBoundsBuffer != null)
                s_AABBBoundsBuffer.Release();

            if (s_ConvexBoundsBuffer != null)
                s_ConvexBoundsBuffer.Release();

            if (s_LightDataBuffer != null)
                s_LightDataBuffer.Release();

            ReleaseResolutionDependentBuffers();

            if (s_DirLightList != null)
                s_DirLightList.Release();

            if (enableClustered)
            {
                if (s_GlobalLightListAtomic != null)
                    s_GlobalLightListAtomic.Release();
            }
        }

        public override void Rebuild()
        {
            ClearComputeBuffers();

            s_GBufferAlbedo = Shader.PropertyToID("_CameraGBufferTexture0");
            s_GBufferSpecRough = Shader.PropertyToID("_CameraGBufferTexture1");
            s_GBufferNormal = Shader.PropertyToID("_CameraGBufferTexture2");
            s_GBufferEmission = Shader.PropertyToID("_CameraGBufferTexture3");
            s_GBufferZ = Shader.PropertyToID("_CameraGBufferZ"); // used while rendering into G-buffer+
            s_CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture"); // copy of that for later sampling in shaders
            s_CameraTarget = Shader.PropertyToID("_CameraTarget");

            m_DeferredMaterial = new Material(deferredShader);
            m_DeferredReflectionMaterial = new Material(deferredReflectionShader);
            m_DeferredMaterial.hideFlags = HideFlags.HideAndDontSave;
            m_DeferredReflectionMaterial.hideFlags = HideFlags.HideAndDontSave;

            s_GenAABBKernel = buildScreenAABBShader.FindKernel("ScreenBoundsAABB");
            s_GenListPerTileKernel = buildPerTileLightListShader.FindKernel("TileLightListGen");
            s_AABBBoundsBuffer = new ComputeBuffer(2 * MaxNumLights, 3 * sizeof(float));
            s_ConvexBoundsBuffer = new ComputeBuffer(MaxNumLights, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SFiniteLightBound)));
            s_LightDataBuffer = new ComputeBuffer(MaxNumLights, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SFiniteLightData)));
            s_DirLightList = new ComputeBuffer(MaxNumDirLights, System.Runtime.InteropServices.Marshal.SizeOf(typeof(DirectionalLight)));

            buildScreenAABBShader.SetBuffer(s_GenAABBKernel, "g_data", s_ConvexBoundsBuffer);
            //m_BuildScreenAABBShader.SetBuffer(kGenAABBKernel, "g_vBoundsBuffer", m_aabbBoundsBuffer);
            m_DeferredMaterial.SetBuffer("g_vLightData", s_LightDataBuffer);
            m_DeferredMaterial.SetBuffer("g_dirLightData", s_DirLightList);
            m_DeferredReflectionMaterial.SetBuffer("g_vLightData", s_LightDataBuffer);

            buildPerTileLightListShader.SetBuffer(s_GenListPerTileKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
            buildPerTileLightListShader.SetBuffer(s_GenListPerTileKernel, "g_vLightData", s_LightDataBuffer);
            buildPerTileLightListShader.SetBuffer(s_GenListPerTileKernel, "g_data", s_ConvexBoundsBuffer);

            if (enableClustered)
            {
                s_GenListPerVoxelKernel = buildPerVoxelLightListShader.FindKernel(k_UseDepthBuffer ? "TileLightListGen_DepthRT" : "TileLightListGen_NoDepthRT");
                s_ClearVoxelAtomicKernel = buildPerVoxelLightListShader.FindKernel("ClearAtomic");
                buildPerVoxelLightListShader.SetBuffer(s_GenListPerVoxelKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
                buildPerVoxelLightListShader.SetBuffer(s_GenListPerVoxelKernel, "g_vLightData", s_LightDataBuffer);
                buildPerVoxelLightListShader.SetBuffer(s_GenListPerVoxelKernel, "g_data", s_ConvexBoundsBuffer);

                s_GlobalLightListAtomic = new ComputeBuffer(1, sizeof(uint));
            }

            m_CookieTexArray = new TextureCache2D();
            m_CubeCookieTexArray = new TextureCacheCubemap();
            m_CubeReflTexArray = new TextureCacheCubemap();
            m_CookieTexArray.AllocTextureArray(8, (int)m_TextureSettings.spotCookieSize, (int)m_TextureSettings.spotCookieSize, TextureFormat.RGBA32, true);
            m_CubeCookieTexArray.AllocTextureArray(4, (int)m_TextureSettings.pointCookieSize, TextureFormat.RGBA32, true);
            m_CubeReflTexArray.AllocTextureArray(64, (int)m_TextureSettings.reflectionCubemapSize, TextureFormat.BC6H, true);

            //m_DeferredMaterial.SetTexture("_spotCookieTextures", m_cookieTexArray.GetTexCache());
            //m_DeferredMaterial.SetTexture("_pointCookieTextures", m_cubeCookieTexArray.GetTexCache());
            //m_DeferredReflectionMaterial.SetTexture("_reflCubeTextures", m_cubeReflTexArray.GetTexCache());

            m_MatWorldToShadow = new Matrix4x4[k_MaxLights * k_MaxShadowmapPerLights];
            m_DirShadowSplitSpheres = new Vector4[k_MaxDirectionalSplit];
            m_Shadow3X3PCFTerms = new Vector4[4];
            m_ShadowPass = new ShadowRenderPass(m_ShadowSettings);

            m_SkyboxHelper = new SkyboxHelper();
            m_SkyboxHelper.CreateMesh();

            m_BlitMaterial = new Material(finalPassShader) { hideFlags = HideFlags.HideAndDontSave };

            s_LightList = null;
        }

        void OnDisable()
        {
            // RenderLoop.renderLoopDelegate -= ExecuteRenderLoop;
            if (m_DeferredMaterial) DestroyImmediate(m_DeferredMaterial);
            if (m_DeferredReflectionMaterial) DestroyImmediate(m_DeferredReflectionMaterial);
            if (m_BlitMaterial) DestroyImmediate(m_BlitMaterial);

            m_CookieTexArray.Release();
            m_CubeCookieTexArray.Release();
            m_CubeReflTexArray.Release();

            s_AABBBoundsBuffer.Release();
            s_ConvexBoundsBuffer.Release();
            s_LightDataBuffer.Release();
            ReleaseResolutionDependentBuffers();
            s_DirLightList.Release();

            if (enableClustered)
            {
                s_GlobalLightListAtomic.Release();
            }
        }

        static void SetupGBuffer(int width, int height, CommandBuffer cmd)
        {
            var format10 = RenderTextureFormat.ARGB32;
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB2101010))
                format10 = RenderTextureFormat.ARGB2101010;
            var formatHDR = RenderTextureFormat.DefaultHDR;

            //@TODO: cleanup, right now only because we want to use unmodified Standard shader that encodes emission differently based on HDR or not,
            // so we make it think we always render in HDR
            cmd.EnableShaderKeyword ("UNITY_HDR_ON");

            //@TODO: GetGraphicsCaps().buggyMRTSRGBWriteFlag
            cmd.GetTemporaryRT(s_GBufferAlbedo, width, height, 0, FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            cmd.GetTemporaryRT(s_GBufferSpecRough, width, height, 0, FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            cmd.GetTemporaryRT(s_GBufferNormal, width, height, 0, FilterMode.Point, format10, RenderTextureReadWrite.Linear);
            cmd.GetTemporaryRT(s_GBufferEmission, width, height, 0, FilterMode.Point, formatHDR, RenderTextureReadWrite.Linear);
            cmd.GetTemporaryRT(s_GBufferZ, width, height, 24, FilterMode.Point, RenderTextureFormat.Depth);
            cmd.GetTemporaryRT(s_CameraDepthTexture, width, height, 24, FilterMode.Point, RenderTextureFormat.Depth);

            cmd.GetTemporaryRT(s_CameraTarget, width, height, 0, FilterMode.Point, formatHDR, RenderTextureReadWrite.Default);

            var colorMRTs = new RenderTargetIdentifier[4] { s_GBufferAlbedo, s_GBufferSpecRough, s_GBufferNormal, s_GBufferEmission };
            cmd.SetRenderTarget(colorMRTs, new RenderTargetIdentifier(s_GBufferZ));
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));

            //@TODO: render VR occlusion mesh
        }

        static void RenderGBuffer(CullResults cull, Camera camera, RenderLoop loop)
        {
            // setup GBuffer for rendering
            var cmd = new CommandBuffer { name = "Create G-Buffer" };
            SetupGBuffer(camera.pixelWidth, camera.pixelHeight, cmd);
            loop.ExecuteCommandBuffer(cmd);
            cmd.Dispose();

            // render opaque objects using Deferred pass
            var settings = new DrawRendererSettings(cull, camera, new ShaderPassName("Deferred"))
            {
                sorting = {sortOptions = SortOptions.SortByMaterialThenMesh},
                rendererConfiguration = RendererConfiguration.PerObjectLightmaps
            };

            //@TODO: need to get light probes + LPPV too?
            settings.inputCullingOptions.SetQueuesOpaque();
            settings.rendererConfiguration = RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightProbe;
            loop.DrawRenderers(ref settings);
        }

        void RenderForward(CullResults cull, Camera camera, RenderLoop loop, bool opaquesOnly)
        {
            var cmd = new CommandBuffer { name = opaquesOnly ? "Prep Opaques Only Forward Pass" : "Prep Forward Pass" };

            bool useFptl = opaquesOnly && UsingFptl();     // requires depth pre-pass for forward opaques!

            bool haveTiledSolution = opaquesOnly || enableClustered;
            cmd.EnableShaderKeyword(haveTiledSolution ? "TILED_FORWARD" : "REGULAR_FORWARD" );
            cmd.SetGlobalFloat("g_isOpaquesOnlyEnabled", useFptl ? 1 : 0);      // leaving this as a dynamic toggle for now for forward opaques to keep shader variants down.
            cmd.SetGlobalBuffer("g_vLightListGlobal", useFptl ? s_LightList : s_PerVoxelLightLists);

            loop.ExecuteCommandBuffer(cmd);
            cmd.Dispose();

            // render opaque objects using Deferred pass
            var settings = new DrawRendererSettings(cull, camera, new ShaderPassName("ForwardSinglePass"))
            {
                sorting = { sortOptions = SortOptions.SortByMaterialThenMesh }
            };
            settings.rendererConfiguration = RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightProbe;
            if (opaquesOnly) settings.inputCullingOptions.SetQueuesOpaque();
            else settings.inputCullingOptions.SetQueuesTransparent();

            loop.DrawRenderers(ref settings);
        }

        static void DepthOnlyForForwardOpaques(CullResults cull, Camera camera, RenderLoop loop)
        {
            var cmd = new CommandBuffer { name = "Forward Opaques - Depth Only" };
            cmd.SetRenderTarget(new RenderTargetIdentifier(s_GBufferZ));
            loop.ExecuteCommandBuffer(cmd);
            cmd.Dispose();

            // render opaque objects using Deferred pass
            var settings = new DrawRendererSettings(cull, camera, new ShaderPassName("DepthOnly"))
            {
                sorting = { sortOptions = SortOptions.SortByMaterialThenMesh }
            };
            settings.inputCullingOptions.SetQueuesOpaque();
            loop.DrawRenderers(ref settings);
        }
        
        bool UsingFptl()
        {
            bool isEnabledMSAA = false;
            Debug.Assert((!isEnabledMSAA) || enableClustered);
            bool disableFptl = (disableFptlWhenClustered && enableClustered) || isEnabledMSAA;
            return !disableFptl;
        }

        static void CopyDepthAfterGBuffer(RenderLoop loop)
        {
            var cmd = new CommandBuffer { name = "Copy depth" };
            cmd.CopyTexture(new RenderTargetIdentifier(s_GBufferZ), new RenderTargetIdentifier(s_CameraDepthTexture));
            loop.ExecuteCommandBuffer(cmd);
            cmd.Dispose();
        }

        void DoTiledDeferredLighting(Camera camera, RenderLoop loop)
        {
            var bUseClusteredForDeferred = !UsingFptl();       // doesn't work on reflections yet but will soon
            var cmd = new CommandBuffer();

            m_DeferredMaterial.EnableKeyword(bUseClusteredForDeferred ? "USE_CLUSTERED_LIGHTLIST" : "USE_FPTL_LIGHTLIST");
            m_DeferredReflectionMaterial.EnableKeyword(bUseClusteredForDeferred ? "USE_CLUSTERED_LIGHTLIST" : "USE_FPTL_LIGHTLIST");

            cmd.SetGlobalBuffer("g_vLightListGlobal", bUseClusteredForDeferred ? s_PerVoxelLightLists : s_LightList);       // opaques list (unless MSAA possibly)

            // In case of bUseClusteredForDeferred disable toggle option since we're using m_perVoxelLightLists as opposed to lightList
            if (bUseClusteredForDeferred)
            {
                cmd.SetGlobalFloat("g_isOpaquesOnlyEnabled", 0);
            }

            cmd.name = "DoTiledDeferredLighting";

            //cmd.SetRenderTarget(new RenderTargetIdentifier(kGBufferEmission), new RenderTargetIdentifier(kGBufferZ));


            //cmd.Blit (kGBufferNormal, (RenderTexture)null); // debug: display normals

            cmd.Blit(0, s_CameraTarget, m_DeferredMaterial, 0);
            cmd.Blit(0, s_CameraTarget, m_DeferredReflectionMaterial, 0);

            // Set the intermediate target for compositing (skybox, etc)
            cmd.SetRenderTarget(new RenderTargetIdentifier(s_CameraTarget), new RenderTargetIdentifier(s_CameraDepthTexture));

            loop.ExecuteCommandBuffer(cmd);
            cmd.Dispose();
        }

        static void SetMatrixCS(CommandBuffer cmd, ComputeShader shadercs, string name, Matrix4x4 mat)
        {
            var data = new float[16];

            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                    data[4 * c + r] = mat[r, c];

            cmd.SetComputeFloatParams(shadercs, name, data);
        }

        static int UpdateDirectionalLights(Camera camera, IList<VisibleLight> visibleLights)
        {
            var dirLightCount = 0;
            var lights = new List<DirectionalLight>();
            var worldToView = camera.worldToCameraMatrix;

            for (int nLight = 0; nLight < visibleLights.Count; nLight++)
            {
                var light = visibleLights[nLight];
                if (light.lightType == LightType.Directional)
                {
                    Debug.Assert(dirLightCount < MaxNumDirLights, "Too many directional lights.");

                    var l = new DirectionalLight();

                    var lightToWorld = light.localToWorld;

                    Vector3 lightDir = lightToWorld.GetColumn(2);   // Z axis in world space

                    // represents a left hand coordinate system in world space
                    Vector3 vx = lightToWorld.GetColumn(0);     // X axis in world space
                    Vector3 vy = lightToWorld.GetColumn(1);     // Y axis in world space
                    var vz = lightDir;                      // Z axis in world space

                    vx = worldToView.MultiplyVector(vx);
                    vy = worldToView.MultiplyVector(vy);
                    vz = worldToView.MultiplyVector(vz);

                    l.shadowLightIndex = (light.light.shadows != LightShadows.None) ? (uint)nLight : 0xffffffff;

                    l.lightAxisX = vx;
                    l.lightAxisY = vy;
                    l.lightAxisZ = vz;

                    l.color.Set(light.finalColor.r, light.finalColor.g, light.finalColor.b);
                    l.intensity = light.light.intensity;

                    lights.Add(l);
                    dirLightCount++;
                }
            }
            s_DirLightList.SetData(lights.ToArray());

            return dirLightCount;
        }

        void UpdateShadowConstants(IList<VisibleLight> visibleLights, ref ShadowOutput shadow)
        {
            var nNumLightsIncludingTooMany = 0;

            var numLights = 0;

            var lightShadowIndex_LightParams = new Vector4[k_MaxLights];
            var lightFalloffParams = new Vector4[k_MaxLights];

            for (int nLight = 0; nLight < visibleLights.Count; nLight++)
            {
                nNumLightsIncludingTooMany++;
                if (nNumLightsIncludingTooMany > k_MaxLights)
                    continue;

                var light = visibleLights[nLight];
                var lightType = light.lightType;
                var position = light.light.transform.position;
                var lightDir = light.light.transform.forward.normalized;

                // Setup shadow data arrays
                var hasShadows = shadow.GetShadowSliceCountLightIndex(nLight) != 0;

                if (lightType == LightType.Directional)
                {
                    lightShadowIndex_LightParams[numLights] = new Vector4(0, 0, 1, 1);
                    lightFalloffParams[numLights] = new Vector4(0.0f, 0.0f, float.MaxValue, (float)lightType);

                    if (hasShadows)
                    {
                        for (int s = 0; s < k_MaxDirectionalSplit; ++s)
                        {
                            m_DirShadowSplitSpheres[s] = shadow.directionalShadowSplitSphereSqr[s];
                        }
                    }
                }
                else if (lightType == LightType.Point)
                {
                    lightShadowIndex_LightParams[numLights] = new Vector4(0, 0, 1, 1);
                    lightFalloffParams[numLights] = new Vector4(1.0f, 0.0f, light.range * light.range, (float)lightType);
                }
                else if (lightType == LightType.Spot)
                {
                    lightShadowIndex_LightParams[numLights] = new Vector4(0, 0, 1, 1);
                    lightFalloffParams[numLights] = new Vector4(1.0f, 0.0f, light.range * light.range, (float)lightType);
                }

                if (hasShadows)
                {
                    // Enable shadows
                    lightShadowIndex_LightParams[numLights].x = 1;
                    for (int s = 0; s < shadow.GetShadowSliceCountLightIndex(nLight); ++s)
                    {
                        var shadowSliceIndex = shadow.GetShadowSliceIndex(nLight, s);
                        m_MatWorldToShadow[numLights * k_MaxShadowmapPerLights + s] = shadow.shadowSlices[shadowSliceIndex].shadowTransform.transpose;
                    }
                }

                numLights++;
            }

            // Warn if too many lights found
            if (nNumLightsIncludingTooMany > k_MaxLights)
            {
                if (nNumLightsIncludingTooMany > m_WarnedTooManyLights)
                {
                    Debug.LogError("ERROR! Found " + nNumLightsIncludingTooMany + " runtime lights! Valve renderer supports up to " + k_MaxLights +
                        " active runtime lights at a time!\nDisabling " + (nNumLightsIncludingTooMany - k_MaxLights) + " runtime light" +
                        ((nNumLightsIncludingTooMany - k_MaxLights) > 1 ? "s" : "") + "!\n");
                }
                m_WarnedTooManyLights = nNumLightsIncludingTooMany;
            }
            else
            {
                if (m_WarnedTooManyLights > 0)
                {
                    m_WarnedTooManyLights = 0;
                    Debug.Log("SUCCESS! Found " + nNumLightsIncludingTooMany + " runtime lights which is within the supported number of lights, " + k_MaxLights + ".\n\n");
                }
            }

            // PCF 3x3 Shadows
            var flTexelEpsilonX = 1.0f / m_ShadowSettings.shadowAtlasWidth;
            var flTexelEpsilonY = 1.0f / m_ShadowSettings.shadowAtlasHeight;
            m_Shadow3X3PCFTerms[0] = new Vector4(20.0f / 267.0f, 33.0f / 267.0f, 55.0f / 267.0f, 0.0f);
            m_Shadow3X3PCFTerms[1] = new Vector4(flTexelEpsilonX, flTexelEpsilonY, -flTexelEpsilonX, -flTexelEpsilonY);
            m_Shadow3X3PCFTerms[2] = new Vector4(flTexelEpsilonX, flTexelEpsilonY, 0.0f, 0.0f);
            m_Shadow3X3PCFTerms[3] = new Vector4(-flTexelEpsilonX, -flTexelEpsilonY, 0.0f, 0.0f);
        }

        int GenerateSourceLightBuffers(Camera camera, CullResults inputs)
        {
            var probes = inputs.visibleReflectionProbes;
            //ReflectionProbe[] probes = Object.FindObjectsOfType<ReflectionProbe>();

            var numModels = (int)LightDefinitions.NR_LIGHT_MODELS;
            var numVolTypes = (int)LightDefinitions.MAX_TYPES;
            var numEntries = new int[numModels,numVolTypes];
            var offsets = new int[numModels,numVolTypes];
            var numEntries2nd = new int[numModels,numVolTypes];

            // first pass. Figure out how much we have of each and establish offsets
            foreach (var cl in inputs.visibleLights)
            {
                var volType = cl.lightType==LightType.Spot ? LightDefinitions.SPOT_LIGHT : (cl.lightType==LightType.Point ? LightDefinitions.SPHERE_LIGHT : -1);
                if(volType>=0) ++numEntries[LightDefinitions.DIRECT_LIGHT,volType];
            }

            foreach (var rl in probes)
            {
                var volType = LightDefinitions.BOX_LIGHT;       // always a box for now
                if(rl.texture!=null) ++numEntries[LightDefinitions.REFLECTION_LIGHT,volType];
            }

            // add decals here too similar to the above

            // establish offsets
            for(var m=0; m<numModels; m++)
            {
                offsets[m,0] = m==0 ? 0 : (numEntries[m-1,numVolTypes-1] + offsets[m-1,numVolTypes-1]);
                for(var v=1; v<numVolTypes; v++) offsets[m,v] = numEntries[m,v-1]+offsets[m,v-1];
            }


            var numLights = inputs.visibleLights.Length;
            var numProbes = probes.Length;
            var numVolumes = numLights + numProbes;


            var lightData = new SFiniteLightData[numVolumes];
            var boundData = new SFiniteLightBound[numVolumes];
            var worldToView = camera.worldToCameraMatrix;

            uint shadowLightIndex = 0;
            foreach (var cl in inputs.visibleLights)
            {
                var range = cl.range;

                var lightToWorld = cl.localToWorld;
                //Matrix4x4 worldToLight = l.worldToLocal;

                Vector3 lightPos = lightToWorld.GetColumn(3);

                var bound = new SFiniteLightBound();
                var light = new SFiniteLightData();

                bound.boxAxisX.Set(1, 0, 0);
                bound.boxAxisY.Set(0, 1, 0);
                bound.boxAxisZ.Set(0, 0, 1);
                bound.scaleXY.Set(1.0f, 1.0f);
                bound.radius = range;

                light.flags = 0;
                light.recipRange = 1.0f / range;
                light.color.Set(cl.finalColor.r, cl.finalColor.g, cl.finalColor.b);
                light.sliceIndex = 0;
                light.lightModel = (uint)LightDefinitions.DIRECT_LIGHT;
                light.shadowLightIndex = shadowLightIndex;
                shadowLightIndex++;

                var bHasCookie = cl.light.cookie != null;
                var bHasShadow = cl.light.shadows != LightShadows.None;

                var idxOut = 0;

                if (cl.lightType == LightType.Spot)
                {
                    var isCircularSpot = !bHasCookie;
                    if (!isCircularSpot)    // square spots always have cookie
                    {
                        light.sliceIndex = m_CookieTexArray.FetchSlice(cl.light.cookie);
                    }

                    Vector3 lightDir = lightToWorld.GetColumn(2);   // Z axis in world space

                    // represents a left hand coordinate system in world space
                    Vector3 vx = lightToWorld.GetColumn(0);     // X axis in world space
                    Vector3 vy = lightToWorld.GetColumn(1);     // Y axis in world space
                    var vz = lightDir;                      // Z axis in world space

                    // transform to camera space (becomes a left hand coordinate frame in Unity since Determinant(worldToView)<0)
                    vx = worldToView.MultiplyVector(vx);
                    vy = worldToView.MultiplyVector(vy);
                    vz = worldToView.MultiplyVector(vz);


                    const float pi = 3.1415926535897932384626433832795f;
                    const float degToRad = (float)(pi / 180.0);
                    const float radToDeg = (float)(180.0 / pi);


                    //float sa = cl.GetSpotAngle();     // total field of view from left to right side
                    var sa = radToDeg * (2 * Mathf.Acos(1.0f / cl.invCosHalfSpotAngle));       // spot angle doesn't exist in the structure so reversing it for now.


                    var cs = Mathf.Cos(0.5f * sa * degToRad);
                    var si = Mathf.Sin(0.5f * sa * degToRad);
                    var ta = cs > 0.0f ? (si / cs) : FltMax;

                    var cota = si > 0.0f ? (cs / si) : FltMax;

                    //const float cotasa = l.GetCotanHalfSpotAngle();

                    // apply nonuniform scale to OBB of spot light
                    var squeeze = true;//sa < 0.7f * 90.0f;      // arb heuristic
                    var fS = squeeze ? ta : si;
                    bound.center = worldToView.MultiplyPoint(lightPos + ((0.5f * range) * lightDir));    // use mid point of the spot as the center of the bounding volume for building screen-space AABB for tiled lighting.

                    light.lightAxisX = vx;
                    light.lightAxisY = vy;
                    light.lightAxisZ = vz;

                    // scale axis to match box or base of pyramid
                    bound.boxAxisX = (fS * range) * vx;
                    bound.boxAxisY = (fS * range) * vy;
                    bound.boxAxisZ = (0.5f * range) * vz;

                    // generate bounding sphere radius
                    var fAltDx = si;
                    var fAltDy = cs;
                    fAltDy = fAltDy - 0.5f;
                    //if(fAltDy<0) fAltDy=-fAltDy;

                    fAltDx *= range; fAltDy *= range;

                    var altDist = Mathf.Sqrt(fAltDy * fAltDy + (isCircularSpot ? 1.0f : 2.0f) * fAltDx * fAltDx);
                    bound.radius = altDist > (0.5f * range) ? altDist : (0.5f * range);       // will always pick fAltDist
                    bound.scaleXY = squeeze ? new Vector2(0.01f, 0.01f) : new Vector2(1.0f, 1.0f);

                    // fill up ldata
                    light.lightType = (uint)LightDefinitions.SPOT_LIGHT;
                    light.lightPos = worldToView.MultiplyPoint(lightPos);
                    light.radiusSq = range * range;
                    light.penumbra = cs;
                    light.cotan = cota;
                    light.flags |= (isCircularSpot ? LightDefinitions.IS_CIRCULAR_SPOT_SHAPE : 0);

                    light.flags |= (bHasCookie ? LightDefinitions.HAS_COOKIE_TEXTURE : 0);
                    light.flags |= (bHasShadow ? LightDefinitions.HAS_SHADOW : 0);

                    int i = LightDefinitions.DIRECT_LIGHT, j = LightDefinitions.SPOT_LIGHT;
                    idxOut = numEntries2nd[i,j] + offsets[i,j]; ++numEntries2nd[i,j];
                }
                else if (cl.lightType == LightType.Point)
                {
                    if (bHasCookie)
                    {
                        light.sliceIndex = m_CubeCookieTexArray.FetchSlice(cl.light.cookie);
                    }

                    bound.center = worldToView.MultiplyPoint(lightPos);
                    bound.boxAxisX.Set(range, 0, 0);
                    bound.boxAxisY.Set(0, range, 0);
                    bound.boxAxisZ.Set(0, 0, -range);    // transform to camera space (becomes a left hand coordinate frame in Unity since Determinant(worldToView)<0)
                    bound.scaleXY.Set(1.0f, 1.0f);
                    bound.radius = range;

                    // represents a left hand coordinate system in world space since det(worldToView)<0
                    var lightToView = worldToView * lightToWorld;
                    Vector3 vx = lightToView.GetColumn(0);
                    Vector3 vy = lightToView.GetColumn(1);
                    Vector3 vz = lightToView.GetColumn(2);

                    // fill up ldata
                    light.lightType = (uint)LightDefinitions.SPHERE_LIGHT;
                    light.lightPos = bound.center;
                    light.radiusSq = range * range;

                    light.lightAxisX = vx;
                    light.lightAxisY = vy;
                    light.lightAxisZ = vz;

                    light.flags |= (bHasCookie ? LightDefinitions.HAS_COOKIE_TEXTURE : 0);
                    light.flags |= (bHasShadow ? LightDefinitions.HAS_SHADOW : 0);

                    int i = LightDefinitions.DIRECT_LIGHT, j = LightDefinitions.SPHERE_LIGHT;
                    idxOut = numEntries2nd[i,j] + offsets[i,j]; ++numEntries2nd[i,j];
                }
                else
                {
                    //Assert(false);
                }

                // next light
                if (cl.lightType == LightType.Spot || cl.lightType == LightType.Point)
                {
                    boundData[idxOut] = bound;
                    lightData[idxOut] = light;
                }
            }
            var numLightsOut = offsets[LightDefinitions.DIRECT_LIGHT, numVolTypes-1] + numEntries[LightDefinitions.DIRECT_LIGHT, numVolTypes-1];
            
            // probe.m_BlendDistance
            // Vector3f extents = 0.5*Abs(probe.m_BoxSize);
            // C center of rendered refl box <-- GetComponent (Transform).GetPosition() + m_BoxOffset;
            // cube map capture point: GetComponent (Transform).GetPosition()
            // shader parameter min and max are C+/-(extents+blendDistance)
            foreach (var rl in probes)
            {
                var cubemap = rl.texture;

                // always a box for now
                if (cubemap == null)
                    continue;

                var bndData = new SFiniteLightBound();
                var lgtData = new SFiniteLightData();

                var idxOut = 0;
                lgtData.flags = 0;

                var bnds = rl.bounds;
                var boxOffset = rl.center;                  // reflection volume offset relative to cube map capture point
                var blendDistance = rl.blendDistance;
                float imp = rl.importance;

                var mat = rl.localToWorld;
                //Matrix4x4 mat = rl.transform.localToWorldMatrix;
                Vector3 cubeCapturePos = mat.GetColumn(3);      // cube map capture position in world space


                // implicit in CalculateHDRDecodeValues() --> float ints = rl.intensity;
                var boxProj = (rl.boxProjection != 0);
                var decodeVals = rl.hdr;
                //Vector4 decodeVals = rl.CalculateHDRDecodeValues();

                // C is reflection volume center in world space (NOT same as cube map capture point)
                var e = bnds.extents;       // 0.5f * Vector3.Max(-boxSizes[p], boxSizes[p]);
                //Vector3 C = bnds.center;        // P + boxOffset;
                var C = mat.MultiplyPoint(boxOffset);       // same as commented out line above when rot is identity

                //Vector3 posForShaderParam = bnds.center - boxOffset;    // gives same as rl.GetComponent<Transform>().position;
                var posForShaderParam = cubeCapturePos;        // same as commented out line above when rot is identity
                var combinedExtent = e + new Vector3(blendDistance, blendDistance, blendDistance);

                Vector3 vx = mat.GetColumn(0);
                Vector3 vy = mat.GetColumn(1);
                Vector3 vz = mat.GetColumn(2);

                // transform to camera space (becomes a left hand coordinate frame in Unity since Determinant(worldToView)<0)
                vx = worldToView.MultiplyVector(vx);
                vy = worldToView.MultiplyVector(vy);
                vz = worldToView.MultiplyVector(vz);

                var Cw = worldToView.MultiplyPoint(C);

                if (boxProj) lgtData.flags |= LightDefinitions.IS_BOX_PROJECTED;

                lgtData.lightPos = Cw;
                lgtData.lightAxisX = vx;
                lgtData.lightAxisY = vy;
                lgtData.lightAxisZ = vz;
                lgtData.localCubeCapturePoint = -boxOffset;
                lgtData.probeBlendDistance = blendDistance;

                lgtData.lightIntensity = decodeVals.x;
                lgtData.decodeExp = decodeVals.y;

                lgtData.sliceIndex = m_CubeReflTexArray.FetchSlice(cubemap);

                var delta = combinedExtent - e;
                lgtData.boxInnerDist = e;
                lgtData.boxInvRange.Set(1.0f / delta.x, 1.0f / delta.y, 1.0f / delta.z);

                bndData.center = Cw;
                bndData.boxAxisX = combinedExtent.x * vx;
                bndData.boxAxisY = combinedExtent.y * vy;
                bndData.boxAxisZ = combinedExtent.z * vz;
                bndData.scaleXY.Set(1.0f, 1.0f);
                bndData.radius = combinedExtent.magnitude;

                // fill up ldata
                lgtData.lightType = (uint)LightDefinitions.BOX_LIGHT;
                lgtData.lightModel = (uint)LightDefinitions.REFLECTION_LIGHT;


                int i = LightDefinitions.REFLECTION_LIGHT, j = LightDefinitions.BOX_LIGHT;
                idxOut = numEntries2nd[i,j] + offsets[i,j]; ++numEntries2nd[i,j];
                boundData[idxOut] = bndData;
                lightData[idxOut] = lgtData;
            }

            var numProbesOut = offsets[LightDefinitions.REFLECTION_LIGHT, numVolTypes-1] + numEntries[LightDefinitions.REFLECTION_LIGHT, numVolTypes-1];
            for(var m=0; m<numModels; m++)
            {
                for(var v=0; v<numVolTypes; v++)
                    Debug.Assert(numEntries[m,v]==numEntries2nd[m, v], "count mismatch on second pass!");
            }

            s_ConvexBoundsBuffer.SetData(boundData);
            s_LightDataBuffer.SetData(lightData);


            return numLightsOut + numProbesOut;
        }

        public override void Render(Camera[] cameras, RenderLoop renderLoop)
        {
            foreach (var camera in cameras)
            {
                CullingParameters cullingParams;
                if (!CullResults.GetCullingParameters(camera, out cullingParams))
                    continue;

                m_ShadowPass.UpdateCullingParameters(ref cullingParams);

                var cullResults = CullResults.Cull(ref cullingParams, renderLoop);
                ExecuteRenderLoop(camera, cullResults, renderLoop);
            }

            renderLoop.Submit();
        }

        void FinalPass(RenderLoop loop)
        {
            var cmd = new CommandBuffer { name = "FinalPass" };
            cmd.Blit(s_CameraTarget, BuiltinRenderTextureType.CameraTarget, m_BlitMaterial, 0);
            loop.ExecuteCommandBuffer(cmd);
            cmd.Dispose();
        }

        void ExecuteRenderLoop(Camera camera, CullResults cullResults, RenderLoop loop)
        {
            var w = camera.pixelWidth;
            var h = camera.pixelHeight;

            ResizeIfNecessary(w, h);

            // do anything we need to do upon a new frame.
            NewFrame ();

            ShadowOutput shadows;
            m_ShadowPass.Render(loop, cullResults, out shadows);

            //m_DeferredMaterial.SetInt("_SrcBlend", camera.hdr ? (int)BlendMode.One : (int)BlendMode.DstColor);
            //m_DeferredMaterial.SetInt("_DstBlend", camera.hdr ? (int)BlendMode.One : (int)BlendMode.Zero);
            //m_DeferredReflectionMaterial.SetInt("_SrcBlend", camera.hdr ? (int)BlendMode.One : (int)BlendMode.DstColor);
            //m_DeferredReflectionMaterial.SetInt("_DstBlend", camera.hdr ? (int)BlendMode.One : (int)BlendMode.Zero);
            loop.SetupCameraProperties(camera);

            UpdateShadowConstants (cullResults.visibleLights, ref shadows);

            RenderGBuffer(cullResults, camera, loop);

            DepthOnlyForForwardOpaques(cullResults, camera, loop);

            //@TODO: render forward-only objects into depth buffer
            CopyDepthAfterGBuffer(loop);
            //@TODO: render reflection probes

            //RenderLighting(camera, inputs, loop);

            //
            var proj = camera.projectionMatrix;
            var temp = new Matrix4x4();
            temp.SetRow(0, new Vector4(1.0f, 0.0f, 0.0f, 0.0f));
            temp.SetRow(1, new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
            temp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
            temp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            var projh = temp * proj;
            var invProjh = projh.inverse;

            temp.SetRow(0, new Vector4(0.5f * w, 0.0f, 0.0f, 0.5f * w));
            temp.SetRow(1, new Vector4(0.0f, 0.5f * h, 0.0f, 0.5f * h));
            temp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
            temp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            var projscr = temp * proj;
            var invProjscr = projscr.inverse;


            var numLights = GenerateSourceLightBuffers(camera, cullResults);


            var numTilesX = (w + 15) / 16;
            var numTilesY = (h + 15) / 16;
            //ComputeBuffer lightList = new ComputeBuffer(nrTilesX * nrTilesY * (32 / 2), sizeof(uint));


            var cmd = new CommandBuffer() { name = "Build light list" };
            
            // generate screen-space AABBs (used for both fptl and clustered).
            cmd.SetComputeIntParam(buildScreenAABBShader, "g_iNrVisibLights", numLights);
            SetMatrixCS(cmd, buildScreenAABBShader, "g_mProjection", projh);
            SetMatrixCS(cmd, buildScreenAABBShader, "g_mInvProjection", invProjh);
            cmd.SetComputeBufferParam(buildScreenAABBShader, s_GenAABBKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
            cmd.DispatchCompute(buildScreenAABBShader, s_GenAABBKernel, (numLights + 7) / 8, 1, 1);

            if( UsingFptl() )
            {
                cmd.SetComputeIntParams(buildPerTileLightListShader, "g_viDimensions", new int[2] { w, h });
                cmd.SetComputeIntParam(buildPerTileLightListShader, "g_iNrVisibLights", numLights);
                SetMatrixCS(cmd, buildPerTileLightListShader, "g_mScrProjection", projscr);
                SetMatrixCS(cmd, buildPerTileLightListShader, "g_mInvScrProjection", invProjscr);
                cmd.SetComputeTextureParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_depth_tex", new RenderTargetIdentifier(s_CameraDepthTexture));
                cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_vLightList", s_LightList);
                cmd.DispatchCompute(buildPerTileLightListShader, s_GenListPerTileKernel, numTilesX, numTilesY, 1);
            }

            if (enableClustered)
            {
                VoxelLightListGeneration(cmd, camera, numLights, projscr, invProjscr);
            }

            loop.ExecuteCommandBuffer(cmd);
            cmd.Dispose();

            var numDirLights = UpdateDirectionalLights(camera, cullResults.visibleLights);

            // Push all global params
            PushGlobalParams(camera, loop, camera.cameraToWorldMatrix, projscr, invProjscr, numDirLights);

            // do deferred lighting
            DoTiledDeferredLighting(camera, loop);

            RenderForward(cullResults, camera, loop, true);    // opaques only (requires a depth pre-pass)

            m_SkyboxHelper.Draw(loop, camera);

            if(enableClustered) RenderForward(cullResults, camera, loop, false);    // transparencies atm. requires clustered until we get traditional forward

            FinalPass(loop);
        }

        void NewFrame()
        {
            // update texture caches
            m_CookieTexArray.NewFrame();
            m_CubeCookieTexArray.NewFrame();
            m_CubeReflTexArray.NewFrame();

            //m_DeferredMaterial.SetTexture("_spotCookieTextures", m_cookieTexArray.GetTexCache());
            //m_DeferredMaterial.SetTexture("_pointCookieTextures", m_cubeCookieTexArray.GetTexCache());
            //m_DeferredReflectionMaterial.SetTexture("_reflCubeTextures", m_cubeReflTexArray.GetTexCache());
        }

        void ResizeIfNecessary(int curWidth, int curHeight)
        {
            if (curWidth != s_WidthOnRecord || curHeight != s_HeightOnRecord || s_LightList == null)
            {
                if (s_WidthOnRecord > 0 && s_HeightOnRecord > 0)
                    ReleaseResolutionDependentBuffers();

                AllocResolutionDependentBuffers(curWidth, curHeight);

                // update recorded window resolution
                s_WidthOnRecord = curWidth;
                s_HeightOnRecord = curHeight;
            }
        }

        void ReleaseResolutionDependentBuffers()
        {
            if (s_LightList != null)
                s_LightList.Release();

            if (enableClustered)
            {
                if (s_PerVoxelLightLists != null)
                    s_PerVoxelLightLists.Release();

                if (s_PerVoxelOffset != null)
                    s_PerVoxelOffset.Release();

                if (k_UseDepthBuffer && s_PerTileLogBaseTweak != null)
                    s_PerTileLogBaseTweak.Release();
            }
        }

        int NumLightIndicesPerClusteredTile()
        {
            return 8 * (1 << k_Log2NumClusters);       // total footprint for all layers of the tile (measured in light index entries)
        }

        void AllocResolutionDependentBuffers(int width, int height)
        {
            var nrTilesX = (width + 15) / 16;
            var nrTilesY = (height + 15) / 16;
            var nrTiles = nrTilesX * nrTilesY;
            const int capacityUShortsPerTile = 32;
            const int dwordsPerTile = (capacityUShortsPerTile + 1) >> 1;        // room for 31 lights and a nrLights value.

            s_LightList = new ComputeBuffer(LightDefinitions.NR_LIGHT_MODELS * dwordsPerTile * nrTiles, sizeof(uint));       // enough list memory for a 4k x 4k display

            if (enableClustered)
            {
                s_PerVoxelOffset = new ComputeBuffer(LightDefinitions.NR_LIGHT_MODELS * (1 << k_Log2NumClusters) * nrTiles, sizeof(uint));
                s_PerVoxelLightLists = new ComputeBuffer(NumLightIndicesPerClusteredTile() * nrTiles, sizeof(uint));

                if (k_UseDepthBuffer)
                {
                    s_PerTileLogBaseTweak = new ComputeBuffer(nrTiles, sizeof(float));
                }
            }
        }

        void VoxelLightListGeneration(CommandBuffer cmd, Camera camera, int numLights, Matrix4x4 projscr, Matrix4x4 invProjscr)
        {
            // clear atomic offset index
            cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_ClearVoxelAtomicKernel, "g_LayeredSingleIdxBuffer", s_GlobalLightListAtomic);
            cmd.DispatchCompute(buildPerVoxelLightListShader, s_ClearVoxelAtomicKernel, 1, 1, 1);

            cmd.SetComputeIntParam(buildPerVoxelLightListShader, "g_iNrVisibLights", numLights);
            SetMatrixCS(cmd, buildPerVoxelLightListShader, "g_mScrProjection", projscr);
            SetMatrixCS(cmd, buildPerVoxelLightListShader, "g_mInvScrProjection", invProjscr);

            cmd.SetComputeIntParam(buildPerVoxelLightListShader, "g_iLog2NumClusters", k_Log2NumClusters);

            //Vector4 v2_near = invProjscr * new Vector4(0.0f, 0.0f, 0.0f, 1.0f);
            //Vector4 v2_far = invProjscr * new Vector4(0.0f, 0.0f, 1.0f, 1.0f);
            //float nearPlane2 = -(v2_near.z/v2_near.w);
            //float farPlane2 = -(v2_far.z/v2_far.w);
            var nearPlane = camera.nearClipPlane;
            var farPlane = camera.farClipPlane;
            cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fNearPlane", nearPlane);
            cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fFarPlane", farPlane);

            const float C = (float)(1 << k_Log2NumClusters);
            var geomSeries = (1.0 - Mathf.Pow(k_ClustLogBase, C)) / (1 - k_ClustLogBase);        // geometric series: sum_k=0^{C-1} base^k
            m_ClustScale = (float)(geomSeries / (farPlane - nearPlane));

            cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fClustScale", m_ClustScale);
            cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fClustBase", k_ClustLogBase);

            cmd.SetComputeTextureParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_depth_tex", new RenderTargetIdentifier(s_CameraDepthTexture));
            cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_vLayeredLightList", s_PerVoxelLightLists);
            cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_LayeredOffset", s_PerVoxelOffset);
            cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_LayeredSingleIdxBuffer", s_GlobalLightListAtomic);

            if (k_UseDepthBuffer)
            {
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_logBaseBuffer", s_PerTileLogBaseTweak);
            }

            var numTilesX = (camera.pixelWidth + 15) / 16;
            var numTilesY = (camera.pixelHeight + 15) / 16;
            cmd.DispatchCompute(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, numTilesX, numTilesY, 1);
        }

        void PushGlobalParams(Camera camera, RenderLoop loop, Matrix4x4 viewToWorld, Matrix4x4 scrProj, Matrix4x4 incScrProj, int numDirLights)
        {
            var cmd = new CommandBuffer { name = "Push Global Parameters" };

            cmd.SetGlobalFloat("g_widthRT", (float)camera.pixelWidth);
            cmd.SetGlobalFloat("g_heightRT", (float)camera.pixelHeight);

            cmd.SetGlobalMatrix("g_mViewToWorld", viewToWorld);
            cmd.SetGlobalMatrix("g_mWorldToView", viewToWorld.inverse);
            cmd.SetGlobalMatrix("g_mScrProjection", scrProj);
            cmd.SetGlobalMatrix("g_mInvScrProjection", incScrProj);

            cmd.SetGlobalBuffer("g_vLightData", s_LightDataBuffer);

            cmd.SetGlobalTexture("_spotCookieTextures", m_CookieTexArray.GetTexCache());
            cmd.SetGlobalTexture("_pointCookieTextures", m_CubeCookieTexArray.GetTexCache());
            cmd.SetGlobalTexture("_reflCubeTextures", m_CubeReflTexArray.GetTexCache());

            var topCube = ReflectionProbe.GetDefaultCubemapIBL();
            var defdecode = ReflectionProbe.CalculateHDRDecodeValuesForDefaultTexture();
            cmd.SetGlobalTexture("_reflRootCubeTexture", topCube);
            cmd.SetGlobalFloat("_reflRootHdrDecodeMult", defdecode.x);
            cmd.SetGlobalFloat("_reflRootHdrDecodeExp", defdecode.y);

            if (enableClustered)
            {
                cmd.SetGlobalFloat("g_fClustScale", m_ClustScale);
                cmd.SetGlobalFloat("g_fClustBase", k_ClustLogBase);
                cmd.SetGlobalFloat("g_fNearPlane", camera.nearClipPlane);
                cmd.SetGlobalFloat("g_fFarPlane", camera.farClipPlane);
                cmd.SetGlobalFloat("g_iLog2NumClusters", k_Log2NumClusters);


                cmd.SetGlobalFloat("g_isLogBaseBufferEnabled", k_UseDepthBuffer ? 1 : 0);

                cmd.SetGlobalBuffer("g_vLayeredOffsetsBuffer", s_PerVoxelOffset);
                if (k_UseDepthBuffer)
                {
                    cmd.SetGlobalBuffer("g_logBaseBuffer", s_PerTileLogBaseTweak);
                }
            }

            cmd.SetGlobalFloat("g_nNumDirLights", numDirLights);
            cmd.SetGlobalBuffer("g_dirLightData", s_DirLightList);

            // Shadow constants
            cmd.SetGlobalMatrixArray("g_matWorldToShadow", m_MatWorldToShadow);
            cmd.SetGlobalVectorArray("g_vDirShadowSplitSpheres", m_DirShadowSplitSpheres);
            cmd.SetGlobalVector("g_vShadow3x3PCFTerms0", m_Shadow3X3PCFTerms[0]);
            cmd.SetGlobalVector("g_vShadow3x3PCFTerms1", m_Shadow3X3PCFTerms[1]);
            cmd.SetGlobalVector("g_vShadow3x3PCFTerms2", m_Shadow3X3PCFTerms[2]);
            cmd.SetGlobalVector("g_vShadow3x3PCFTerms3", m_Shadow3X3PCFTerms[3]);

            loop.ExecuteCommandBuffer(cmd);
            cmd.Dispose();
        }
    }
}
