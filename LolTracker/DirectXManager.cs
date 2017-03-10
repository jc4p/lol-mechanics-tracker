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

        public bool IsRecording { get; private set; }
        private Task recordingTask;
        private ScreenCaptureDelegate.OnFrameReady frameListener;

        private BlockingCollection<FrameUpdateInfo> recordingQueue = new BlockingCollection<FrameUpdateInfo>(1);
        private BlockingCollection<FrameUpdateInfo> processingQueue = new BlockingCollection<FrameUpdateInfo>(1);

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
            
            D3D11.Device.CreateWithSwapChain(factory.Adapters[0], D3D11.DeviceCreationFlags.SingleThreaded, new FeatureLevel[] { FeatureLevel.Level_11_1 }, swapChainDesc, out d3dDevice, out swapChain);
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

        public BlockingCollection<FrameUpdateInfo> GetRecorder()
        {
            return recordingQueue;
        }

        public BlockingCollection<FrameUpdateInfo> GetProcessor()
        {
            return processingQueue;

        }
        public void StartRecording(ScreenCaptureDelegate.OnFrameReady listener)
        {
            IsRecording = true;
            frameListener = listener;
            if (recordingQueue.Count == 0)
                recordingQueue.Add(new FrameUpdateInfo());
            recordingTask = new Task(new Action(Record));
            recordingTask.Start();
        }

        public void StopRecording()
        {
            IsRecording = false;
        }

        private void Record()
        {
            while (IsRecording)
            {
                FrameUpdateInfo update = recordingQueue.Take();
                OutputDuplicateFrameInformation duplicateFrameInformation;
                try
                {
                    duplicatedOutput.AcquireNextFrame(1000, out duplicateFrameInformation, out screenResource);
                }
                catch (SharpDX.SharpDXException e)
                {
                    Console.WriteLine("Failed to acquire frame: {0}", e);
                    if (e.ResultCode.Code == ResultCode.WaitTimeout.Result.Code)
                    {
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
                    update.MovedRegions = new MovedRegion[moveRectsLen / Marshal.SizeOf(typeof(OutputDuplicateMoveRectangle))];
                    for (int i = 0; i < update.MovedRegions.Length; i++)
                    {
                        update.MovedRegions[i] = new MovedRegion
                        {
                            start = new Point(moveRects[i].SourcePoint.X, moveRects[i].SourcePoint.Y),
                            to = new Rect()
                            {
                                left = moveRects[i].DestinationRect.Left,
                                top = moveRects[i].DestinationRect.Top,
                                right = moveRects[i].DestinationRect.Right,
                                bottom = moveRects[i].DestinationRect.Bottom
                            }
                        };
                    }

                    // get dirty rects
                    int dirtyRectsLen = 0;
                    SharpDX.Mathematics.Interop.RawRectangle[] dirtyRects = new SharpDX.Mathematics.Interop.RawRectangle[duplicateFrameInformation.TotalMetadataBufferSize];
                    duplicatedOutput.GetFrameDirtyRects(dirtyRects.Length, dirtyRects, out dirtyRectsLen);
                    update.DirtyRects = new Rect[dirtyRectsLen / Marshal.SizeOf(typeof(SharpDX.Mathematics.Interop.RawRectangle))];
                    for (int i = 0; i < update.DirtyRects.Length; i++)
                    {
                        update.DirtyRects[i] = new Rect
                        {
                            left = dirtyRects[i].Left,
                            top = dirtyRects[i].Top,
                            right = dirtyRects[i].Right,
                            bottom = dirtyRects[i].Bottom
                        };
                    }
                }
                else
                {
                    update.MovedRegions = new MovedRegion[0];
                    update.DirtyRects = new Rect[0];
                }

                // copy resource into memory that can be accessed by the CPU
                d3dDeviceContext.CopyResource(screenResource.QueryInterface<D3D11.Resource>(), screenTexture);
                // cast from texture to surface, so we can access its bytes
                screenSurface = screenTexture.QueryInterface<Surface>();
                // map the resource to access it
                screenSurface.Map(MapFlags.Read, out screenDataStream);
                
                // Read it!
                if (update.LastAcquiredFrame == null)
                {
                    update.LastAcquiredFrame = new Bitmap(screenTexture.Description.Width, screenTexture.Description.Height, PixelFormat.Format32bppArgb);
                }
                var BoundsRect = new Rectangle(0, 0, update.LastAcquiredFrame.Width, update.LastAcquiredFrame.Height);
                BitmapData bmpData = update.LastAcquiredFrame.LockBits(BoundsRect, ImageLockMode.WriteOnly, update.LastAcquiredFrame.PixelFormat);

                screenDataStream.Read(bmpData.Scan0, 0, bmpData.Stride * update.LastAcquiredFrame.Height);
                update.LastAcquiredFrame.UnlockBits(bmpData);
                
                // free resources
                screenDataStream.Close();
                screenSurface.Unmap();
                screenSurface.Dispose();
                if (screenResource != null)
                    screenResource.Dispose();
                duplicatedOutput.ReleaseFrame();

                // Add to the queue, and we'll wait until we're needed again (hopefully)
                processingQueue.Add(update);
                frameListener();
            }
        }

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

        public class FrameUpdateInfo
        {
            public Bitmap LastAcquiredFrame { get; set; }
            public MovedRegion[] MovedRegions { get; set; }
            public Rect[] DirtyRects { get; set; }
        }

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
