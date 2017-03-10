using SharpDX.Direct3D9;
using SharpDX.Mathematics;
using EasyHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using LolTracker.Capture;
using System.Drawing;
using SharpDX.Mathematics.Interop;
using System.Runtime.Remoting.Channels.Ipc;
using Direct3DCapture;
using System.Threading;

namespace LolTracker
{
    // https://github.com/spazzarama/Direct3DHook/blob/master/Capture/EntryPoint.cs
    public class D3d9Injector : IEntryPoint
    {
        public bool IsRecording { get; private set; }
        private bool HasDirect3D9ExSupport { get; set; }

        private CaptureInterface Interface;
        private IpcServerChannel Gateway = null;
        private ManualResetEvent _init_wait = new ManualResetEvent(false);
        protected readonly CaptureInterface.ClientCaptureInterfaceEventProxy InterfaceEventProxy = new CaptureInterface.ClientCaptureInterfaceEventProxy();
        IpcServerChannel _clientServerChannel = null;

        // #TODO:
        // This is only true if we have Direct3D9Ex support AND we're choosing to
        // use Present as the callback hook over EndScene, I think?
        private bool IsUsingPresent { get; set; }

        private List<HookInfo> Hooks = new List<HookInfo>();

        HookInfo<Direct3D9Device_EndSceneDelegate> HookEndScene = null;
        HookInfo<Direct3D9Device_PresentDelegate> HookPresent = null;
        HookInfo<Direct3D9Device_ResetDelegate> HookReset = null;
        HookInfo<Direct3D9DeviceEx_PresentExDelegate> HookPresentEx = null;
        HookInfo<Direct3D9DeviceEx_ResetExDelegate> HookResetEx = null;

        private List<IntPtr> d3dDeviceFunctionAddresses = new List<IntPtr>();

        const int D3D9_DEVICE_METHOD_COUNT = 119;
        const int D3D9Ex_DEVICE_METHOD_COUNT = 15;

        public D3d9Injector(RemoteHooking.IContext context, string channelName)
        {
            Debug.WriteLine("D3D9Injector initalized");
            Interface = RemoteHooking.IpcConnectClient<CaptureInterface>(channelName);

            // If this fails everything is gonna fail
            Interface.Ping();

            Interface.ScreenshotRequested += InterfaceEventProxy.ScreenshotRequestedProxyHandler;
            InterfaceEventProxy.ScreenshotRequested += new ScreenshotRequestedEvent(InterfaceEventProxy_ScreenshotRequested);

            Interface.Message("D3d9 initalized");

            // Attempt to create a IpcServerChannel so that any event handlers on the client will function correctly
            System.Collections.IDictionary properties = new System.Collections.Hashtable();
            properties["name"] = channelName;
            properties["portName"] = channelName + Guid.NewGuid().ToString("N"); // random portName so no conflict with existing channels of channelName

            System.Runtime.Remoting.Channels.BinaryServerFormatterSinkProvider binaryProv = new System.Runtime.Remoting.Channels.BinaryServerFormatterSinkProvider();
            binaryProv.TypeFilterLevel = System.Runtime.Serialization.Formatters.TypeFilterLevel.Full;

            IpcServerChannel _clientServerChannel = new IpcServerChannel(properties, binaryProv);
            System.Runtime.Remoting.Channels.ChannelServices.RegisterChannel(_clientServerChannel, false);

        }

        public void Run(RemoteHooking.IContext context, string channelName)
        {
            // NOTE: We are now running within the target process
            Debug.WriteLine("Begin of Run");
            throw new ArgumentException("" + Interface.ProcessId);
            Interface.Message("Begin of Run");
            InitalizeDirect3D();
        }

        protected virtual void InterfaceEventProxy_ScreenshotRequested()
        {
            Debug.WriteLine("InterfaceEventProxy_ScreenshotRequested");
            Interface.Message("InterfaceEventProxy_ScreenshotRequested");
        }

        private void InitalizeDirect3D()
        {
            InitalizeDirect3D9();
        }

        private void InitalizeDirect3D9()
        {
            // Okay so we gotta find the correct Direct3DDevice so
            Device device;
            using (Direct3D d3d = new Direct3D())
            {
                device = new Device(d3d, 0, DeviceType.NullReference, IntPtr.Zero, CreateFlags.HardwareVertexProcessing,
                    new PresentParameters() { BackBufferWidth = 1, BackBufferHeight = 1 });
                d3dDeviceFunctionAddresses.AddRange(GetVTblAddresses(device.NativePointer, D3D9_DEVICE_METHOD_COUNT));
            }

            try
            {
                using (Direct3DEx d3dEx = new Direct3DEx())
                {
                    var deviceEx = new DeviceEx(d3dEx, 0, DeviceType.NullReference, IntPtr.Zero, CreateFlags.HardwareVertexProcessing,
                        new PresentParameters() { BackBufferWidth = 1, BackBufferHeight = 1 }, new DisplayModeEx() { Width = 800, Height = 600 });
                    d3dDeviceFunctionAddresses.AddRange(GetVTblAddresses(deviceEx.NativePointer, D3D9_DEVICE_METHOD_COUNT, D3D9Ex_DEVICE_METHOD_COUNT));
                    HasDirect3D9ExSupport = true;
                }
            }
            catch (Exception)
            {
                HasDirect3D9ExSupport = false;
            }

            // we want to hook EndScene so we can retrieve the backbuffer
            HookEndScene = new HookInfo<Direct3D9Device_EndSceneDelegate>(d3dDeviceFunctionAddresses[(int)Direct3DDevice9FunctionOrdinals.EndScene],
                new Direct3D9Device_EndSceneDelegate(EndSceneHook), this);
            Hooks.Add(HookEndScene);

            unsafe
            {
                HookPresent = new HookInfo<Direct3D9Device_PresentDelegate>(d3dDeviceFunctionAddresses[(int)Direct3DDevice9FunctionOrdinals.Present],
                    new Direct3D9Device_PresentDelegate(PresentHook), this);
                Hooks.Add(HookPresent);
            }

            HookReset = new HookInfo<Direct3D9Device_ResetDelegate>(d3dDeviceFunctionAddresses[(int)Direct3DDevice9FunctionOrdinals.Reset],
                new Direct3D9Device_ResetDelegate(ResetHook), this);
            Hooks.Add(HookReset);

            if (HasDirect3D9ExSupport)
            {
                unsafe
                {
                    HookPresentEx = new HookInfo<Direct3D9DeviceEx_PresentExDelegate>(d3dDeviceFunctionAddresses[(int)Direct3DDevice9ExFunctionOrdinals.PresentEx],
                        new Direct3D9DeviceEx_PresentExDelegate(PresentExHook), this);
                    Hooks.Add(HookPresentEx);
                }
                HookResetEx = new HookInfo<Direct3D9DeviceEx_ResetExDelegate>(d3dDeviceFunctionAddresses[(int)Direct3DDevice9ExFunctionOrdinals.ResetEx],
                    new Direct3D9DeviceEx_ResetExDelegate(ResetExHook), this);
                Hooks.Add(HookResetEx);
            }

            // Start all the hooks
            Hooks.ForEach(h => h.Create());
            Interface.Message("Initalized");
        }


        private int EndSceneHook(IntPtr devicePtr)
        {
            // #TODO: Do something with devicePtr
            Interface.Message(string.Format("EndSceneHook: {0}", devicePtr));
            return HookEndScene.Original(devicePtr);
        }

        private unsafe int PresentHook(IntPtr devicePtr, Rectangle* pSourceRect, Rectangle* pDestRect, IntPtr hDestWindowOverride, IntPtr pDirtyRegion) {
            // #TODO: something
            Interface.Message(string.Format("PresentHook: {0}", devicePtr));
            return HookPresent.Original(devicePtr, pSourceRect, pSourceRect, hDestWindowOverride, pDirtyRegion);
        }

        private int ResetHook(IntPtr devicePtr, ref PresentParameters presentParameters)
        {
            // #TODO: something
            Interface.Message(string.Format("ResetHook: {0}", devicePtr));
            return HookReset.Original(devicePtr, ref presentParameters);
        }

        private unsafe int PresentExHook(IntPtr devicePtr, Rectangle* pSourceRect, Rectangle* pDestRect, IntPtr hDestWindowOverride, IntPtr pDirtyRegion, Present dwFlags)
        {
            // #TODO: something
            Interface.Message(string.Format("PresentExHook: {0}", devicePtr));
            return HookPresentEx.Original(devicePtr, pSourceRect, pDestRect, hDestWindowOverride, pDirtyRegion, dwFlags);
        }

        private int ResetExHook(IntPtr devicePtr, ref PresentParameters presentParameters, DisplayModeEx displayModeEx)
        {
            // #TODO: something
            Interface.Message(string.Format("ResetExHook: {0}", devicePtr));
            return HookResetEx.Original(devicePtr, ref presentParameters, displayModeEx);
        }

        // From https://github.com/spazzarama/Direct3DHook/blob/master/Capture/Hook/BaseDXHook.cs
        protected IntPtr[] GetVTblAddresses(IntPtr pointer, int numberOfMethods)
        {
            return GetVTblAddresses(pointer, 0, numberOfMethods);
        }

        protected IntPtr[] GetVTblAddresses(IntPtr pointer, int startIndex, int numberOfMethods)
        {
            List<IntPtr> vtblAddresses = new List<IntPtr>();

            IntPtr vTable = Marshal.ReadIntPtr(pointer);
            for (int i = startIndex; i < startIndex + numberOfMethods; i++)
                vtblAddresses.Add(Marshal.ReadIntPtr(vTable, i * IntPtr.Size)); // using IntPtr.Size allows us to support both 32 and 64-bit processes

            return vtblAddresses.ToArray();
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        delegate int Direct3D9Device_EndSceneDelegate(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        private unsafe delegate int Direct3D9Device_PresentDelegate(
            IntPtr devicePtr,
            Rectangle* pSourceRect,
            Rectangle* pDestRect,
            IntPtr hDestWindowOverride,
            IntPtr pDirtyRegion);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        private unsafe delegate int Direct3D9DeviceEx_PresentExDelegate(
            IntPtr devicePtr,
            Rectangle* pSourceRect,
            Rectangle* pDestRect,
            IntPtr hDestWindowOverride,
            IntPtr pDirtyRegion,
            Present dwFlags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        delegate int Direct3D9Device_ResetDelegate(IntPtr device, ref PresentParameters presentParameters);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        private delegate int Direct3D9DeviceEx_ResetExDelegate(IntPtr devicePtr, ref PresentParameters presentParameters, DisplayModeEx displayModeEx);


        // From https://github.com/spazzarama/Direct3DHook/blob/master/Capture/Hook/D3D9.cs
        private enum Direct3DDevice9FunctionOrdinals : short
        {
            QueryInterface = 0,
            AddRef = 1,
            Release = 2,
            TestCooperativeLevel = 3,
            GetAvailableTextureMem = 4,
            EvictManagedResources = 5,
            GetDirect3D = 6,
            GetDeviceCaps = 7,
            GetDisplayMode = 8,
            GetCreationParameters = 9,
            SetCursorProperties = 10,
            SetCursorPosition = 11,
            ShowCursor = 12,
            CreateAdditionalSwapChain = 13,
            GetSwapChain = 14,
            GetNumberOfSwapChains = 15,
            Reset = 16,
            Present = 17,
            GetBackBuffer = 18,
            GetRasterStatus = 19,
            SetDialogBoxMode = 20,
            SetGammaRamp = 21,
            GetGammaRamp = 22,
            CreateTexture = 23,
            CreateVolumeTexture = 24,
            CreateCubeTexture = 25,
            CreateVertexBuffer = 26,
            CreateIndexBuffer = 27,
            CreateRenderTarget = 28,
            CreateDepthStencilSurface = 29,
            UpdateSurface = 30,
            UpdateTexture = 31,
            GetRenderTargetData = 32,
            GetFrontBufferData = 33,
            StretchRect = 34,
            ColorFill = 35,
            CreateOffscreenPlainSurface = 36,
            SetRenderTarget = 37,
            GetRenderTarget = 38,
            SetDepthStencilSurface = 39,
            GetDepthStencilSurface = 40,
            BeginScene = 41,
            EndScene = 42,
            Clear = 43,
            SetTransform = 44,
            GetTransform = 45,
            MultiplyTransform = 46,
            SetViewport = 47,
            GetViewport = 48,
            SetMaterial = 49,
            GetMaterial = 50,
            SetLight = 51,
            GetLight = 52,
            LightEnable = 53,
            GetLightEnable = 54,
            SetClipPlane = 55,
            GetClipPlane = 56,
            SetRenderState = 57,
            GetRenderState = 58,
            CreateStateBlock = 59,
            BeginStateBlock = 60,
            EndStateBlock = 61,
            SetClipStatus = 62,
            GetClipStatus = 63,
            GetTexture = 64,
            SetTexture = 65,
            GetTextureStageState = 66,
            SetTextureStageState = 67,
            GetSamplerState = 68,
            SetSamplerState = 69,
            ValidateDevice = 70,
            SetPaletteEntries = 71,
            GetPaletteEntries = 72,
            SetCurrentTexturePalette = 73,
            GetCurrentTexturePalette = 74,
            SetScissorRect = 75,
            GetScissorRect = 76,
            SetSoftwareVertexProcessing = 77,
            GetSoftwareVertexProcessing = 78,
            SetNPatchMode = 79,
            GetNPatchMode = 80,
            DrawPrimitive = 81,
            DrawIndexedPrimitive = 82,
            DrawPrimitiveUP = 83,
            DrawIndexedPrimitiveUP = 84,
            ProcessVertices = 85,
            CreateVertexDeclaration = 86,
            SetVertexDeclaration = 87,
            GetVertexDeclaration = 88,
            SetFVF = 89,
            GetFVF = 90,
            CreateVertexShader = 91,
            SetVertexShader = 92,
            GetVertexShader = 93,
            SetVertexShaderConstantF = 94,
            GetVertexShaderConstantF = 95,
            SetVertexShaderConstantI = 96,
            GetVertexShaderConstantI = 97,
            SetVertexShaderConstantB = 98,
            GetVertexShaderConstantB = 99,
            SetStreamSource = 100,
            GetStreamSource = 101,
            SetStreamSourceFreq = 102,
            GetStreamSourceFreq = 103,
            SetIndices = 104,
            GetIndices = 105,
            CreatePixelShader = 106,
            SetPixelShader = 107,
            GetPixelShader = 108,
            SetPixelShaderConstantF = 109,
            GetPixelShaderConstantF = 110,
            SetPixelShaderConstantI = 111,
            GetPixelShaderConstantI = 112,
            SetPixelShaderConstantB = 113,
            GetPixelShaderConstantB = 114,
            DrawRectPatch = 115,
            DrawTriPatch = 116,
            DeletePatch = 117,
            CreateQuery = 118,
        }

        private enum Direct3DDevice9ExFunctionOrdinals : short
        {
            SetConvolutionMonoKernel = 119,
            ComposeRects = 120,
            PresentEx = 121,
            GetGPUThreadPriority = 122,
            SetGPUThreadPriority = 123,
            WaitForVBlank = 124,
            CheckResourceResidency = 125,
            SetMaximumFrameLatency = 126,
            GetMaximumFrameLatency = 127,
            CheckDeviceState_ = 128,
            CreateRenderTargetEx = 129,
            CreateOffscreenPlainSurfaceEx = 130,
            CreateDepthStencilSurfaceEx = 131,
            ResetEx = 132,
            GetDisplayModeEx = 133,
        }
    }
}
