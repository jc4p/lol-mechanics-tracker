using D3D11 = SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SharpDX.Direct3D;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;

namespace LolTracker
{
    class DirectXManager : IDisposable
    {
        private D3D11.Device d3dDevice;
        private D3D11.DeviceContext d3dDeviceContext;
        private SwapChain swapChain;

        private OutputDuplication duplicatedOutput;
        private D3D11.Texture2D screenTexture;
        private Resource screenResource;
        private SharpDX.DataStream screenDataStream;
        private Surface screenSurface;

        private bool recording = false;
        private Task recordingTask;

        private Bitmap LastAcquiredFrame;
        private MovedRegion[] MovedRegions;
        private Rect[] DirtyRects;

        private OnFrameReady updateListener;

        public Rect GetMainMonitorSize()
        {
            MonitorInfo monitorInfo = new MonitorInfo();
            monitorInfo.cbSize = Marshal.SizeOf(monitorInfo);
            MonitorEnumProc callback = (IntPtr hDesktop, IntPtr hdc, ref Rect prect, int d) => {
                GetMonitorInfo(hDesktop, ref monitorInfo);
                return monitorInfo.dwFlags == 0;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, 0);
            return monitorInfo.rcMonitor;
        }

        public void Init(IntPtr formHandle)
        {
            Rect size = GetMainMonitorSize();
            ModeDescription backBufferDesc = new ModeDescription(size.width, size.height, new Rational(60, 1), Format.R8G8B8A8_UNorm);
            SwapChainDescription swapChainDesc = new SwapChainDescription()
            {
                ModeDescription = backBufferDesc,
                SampleDescription = new SampleDescription(1, 0),
                Usage = Usage.RenderTargetOutput,
                BufferCount = 1,
                OutputHandle = formHandle,
                IsWindowed = true
            };


            Factory1 factory = new Factory1();
            D3D11.Device.CreateWithSwapChain(DriverType.Hardware, D3D11.DeviceCreationFlags.None, swapChainDesc, out d3dDevice, out swapChain);
            d3dDeviceContext = d3dDevice.ImmediateContext;

            // CPU accessible texture with _STAGING
            D3D11.Texture2DDescription textureDesc = new D3D11.Texture2DDescription();
            textureDesc.CpuAccessFlags = D3D11.CpuAccessFlags.Read;
            textureDesc.BindFlags = D3D11.BindFlags.None;
            textureDesc.Format = Format.B8G8R8A8_UNorm;
            textureDesc.Height = size.height;
            textureDesc.Width = size.width;
            textureDesc.OptionFlags = D3D11.ResourceOptionFlags.None;
            textureDesc.MipLevels = 1;
            textureDesc.ArraySize = 1;
            textureDesc.SampleDescription.Count = 1;
            textureDesc.SampleDescription.Quality = 0;
            textureDesc.Usage = D3D11.ResourceUsage.Staging;

            screenTexture = new D3D11.Texture2D(d3dDevice, textureDesc);

            // Actual duplication API
            Output1 output = new Output1(factory.Adapters1[0].Outputs[0].NativePointer);
            duplicatedOutput = output.DuplicateOutput(d3dDevice);
        }

        public bool IsRecording()
        {
            return recording;

        }
        public void StartRecording(OnFrameReady listener)
        {
            updateListener = listener;
            recording = true;
            recordingTask = new Task(new Action(Record));
            recordingTask.Start();
        }

        public void StopRecording()
        {
            updateListener = null;
            recording = false;
        }

        private void Record()
        {
            int i = 0;
            Stopwatch sw = new Stopwatch();
            sw.Start();

            while (recording)
            {
                i++;
                OutputDuplicateFrameInformation duplicateFrameInformation;
                try
                {
                    duplicatedOutput.AcquireNextFrame(1000, out duplicateFrameInformation, out screenResource);
                }
                catch (SharpDX.SharpDXException e)
                {
                    if (e.ResultCode.Code == ResultCode.WaitTimeout.Result.Code)
                    {
                        i--;
                        // keep retrying
                        continue;
                    }
                    else
                    {
                        // just gonna eat all errors for now
                        Console.WriteLine(e);
                        continue;
                    }
                }

                if (duplicateFrameInformation.TotalMetadataBufferSize > 0)
                {
                    // get move rects
                    int moveRectsLen = 0;
                    OutputDuplicateMoveRectangle[] moveRects = new OutputDuplicateMoveRectangle[duplicateFrameInformation.TotalMetadataBufferSize];
                    duplicatedOutput.GetFrameMoveRects(moveRects.Length, moveRects, out moveRectsLen);
                    MovedRegions = new MovedRegion[moveRectsLen / Marshal.SizeOf(typeof(OutputDuplicateMoveRectangle))];
                    for (int j = 0; j < MovedRegions.Length; j++)
                    {
                        MovedRegions[j] = new MovedRegion
                        {
                            start = new Point(moveRects[j].SourcePoint.X, moveRects[j].SourcePoint.Y),
                            to = new Rect()
                            {
                                left = moveRects[j].DestinationRect.Left,
                                top = moveRects[j].DestinationRect.Top,
                                right = moveRects[j].DestinationRect.Right,
                                bottom = moveRects[j].DestinationRect.Bottom
                            }
                        };
                    }

                    // get dirty rects
                    int dirtyRectsLen = 0;
                    SharpDX.Mathematics.Interop.RawRectangle[] dirtyRects = new SharpDX.Mathematics.Interop.RawRectangle[duplicateFrameInformation.TotalMetadataBufferSize];
                    duplicatedOutput.GetFrameDirtyRects(dirtyRects.Length, dirtyRects, out dirtyRectsLen);
                    DirtyRects = new Rect[dirtyRectsLen / Marshal.SizeOf(typeof(SharpDX.Mathematics.Interop.RawRectangle))];
                    for (int j = 0; j < DirtyRects.Length; j++)
                    {
                        DirtyRects[j] = new Rect
                        {
                            left = dirtyRects[j].Left,
                            top = dirtyRects[j].Top,
                            right = dirtyRects[j].Right,
                            bottom = dirtyRects[j].Bottom
                        };
                    }
                }
                else
                {
                    MovedRegions = new MovedRegion[0];
                    DirtyRects = new Rect[0];
                }

                // copy resource into memory that can be accessed by the CPU
                d3dDeviceContext.CopyResource(screenResource.QueryInterface<D3D11.Resource>(), screenTexture);
                // cast from texture to surface, so we can access its bytes
                screenSurface = screenTexture.QueryInterface<Surface>();
                // map the resource to access it
                screenSurface.Map(MapFlags.Read, out screenDataStream);

                // Read it!
                if (LastAcquiredFrame == null)
                {
                    LastAcquiredFrame = new Bitmap(screenTexture.Description.Width, screenTexture.Description.Height, PixelFormat.Format32bppArgb);
                }
                var BoundsRect = new Rectangle(0, 0, LastAcquiredFrame.Width, LastAcquiredFrame.Height);
                BitmapData bmpData = LastAcquiredFrame.LockBits(BoundsRect, ImageLockMode.WriteOnly, LastAcquiredFrame.PixelFormat);

                screenDataStream.Read(bmpData.Scan0, 0, bmpData.Stride * LastAcquiredFrame.Height);
                LastAcquiredFrame.UnlockBits(bmpData);

                // free resources
                screenDataStream.Close();
                screenSurface.Unmap();
                screenSurface.Dispose();
                if (screenResource != null)
                    screenResource.Dispose();
                duplicatedOutput.ReleaseFrame();

                if (updateListener != null)
                {
                    updateListener(LastAcquiredFrame, MovedRegions, DirtyRects);
                }

                // print how many frames we could process within the last second
                // note that this also depends on how often windows will &gt;need&lt; to redraw the interface
                if (sw.ElapsedMilliseconds > 1000)
                {
                    //Console.WriteLine(i + "fps");
                    sw.Reset();
                    sw.Start();
                    i = 0;
                }
            }
        }

        public delegate void OnFrameReady(Bitmap frame, MovedRegion[] movedRegions, Rect[] dirtyRects);
        public delegate void OnFrameError();

        [DllImport("user32")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lpRect, MonitorEnumProc callback, int dwData);
        [DllImport("user32")]
        private static extern bool GetMonitorInfo(IntPtr hdc, ref MonitorInfo monitorInfo);

        public void Dispose()
        {
            swapChain.Dispose();
            d3dDevice.Dispose();
            d3dDeviceContext.Dispose();
        }

        private delegate bool MonitorEnumProc(IntPtr hDesktop, IntPtr hdc, ref Rect pRect, int dwData);

        public struct MovedRegion
        {
            public Point start;
            public Rect to;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int left;
            public int top;
            public int right;
            public int bottom;

            public int width { get { return right - left; } }
            public int height { get { return bottom - top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int cbSize;
            public Rect rcMonitor;
            public Rect rcWork;
            public int dwFlags;
        }
    }
}
