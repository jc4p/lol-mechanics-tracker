using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Direct3DCapture
{
    [Serializable]
    public delegate void RecordingStartedEvent();
    [Serializable]
    public delegate void RecordingStoppedEvent();
    [Serializable]
    public delegate void MessageReceivedEvent(string message);
    [Serializable]
    public delegate void ScreenshotRequestedEvent();
    [Serializable]
    public delegate void ScreenshotReceivedEvent(Screenshot screenshot);
    [Serializable]
    public delegate void DisconnectedEvent();

    // https://github.com/spazzarama/Direct3DHook/blob/master/Capture/Interface/CaptureInterface.cs
    public class CaptureInterface : MarshalByRefObject
    {
        public event RecordingStartedEvent RecordingStarted;
        public event RecordingStoppedEvent RecordingStopped;
        public event MessageReceivedEvent RemoteMessage;
        public event ScreenshotRequestedEvent ScreenshotRequested;
        public event ScreenshotReceivedEvent ScreenshotReceived;
        public event DisconnectedEvent Disconnected;

        public int ProcessId { get; set; }

        public bool IsRecording { get; private set; }

        private object _screenshot_lock = new object();
        private Action<Screenshot> _screenshot_action = null;
        private ManualResetEvent _screenshot_wait = new ManualResetEvent(false);
        private Guid? _screenshot_request_id = null;


        public void Start()
        {
            if (IsRecording)
                return;
            SafeInvokeRecordingStarted();
            IsRecording = true;
        }

        public void Stop()
        {
            if (!IsRecording)
                return;
            SafeInvokeRecordingStopped();
            IsRecording = false;
        }

        public Screenshot GetScreenshot()
        {
            return GetScreenshot(Rectangle.Empty, new TimeSpan(0, 0, 2), null, ImageFormat.Bmp);
        }

        public Screenshot GetScreenshot(Rectangle region, TimeSpan timeout, Size? resize, ImageFormat format)
        {
            lock (_screenshot_lock)
            {
                Screenshot result = null;
                SafeInvokeScreenshotRequested();

                _screenshot_action = (sc) =>
                {
                    try
                    {
                        Interlocked.Exchange(ref result, sc);
                    }
                    catch { }
                    _screenshot_wait.Set();
                };

                _screenshot_wait.WaitOne(timeout);
                _screenshot_action = null;
                return result;
            }
        }
        
        public IAsyncResult BeginGetScreenshot(Rectangle region, TimeSpan timeout, AsyncCallback callback = null, Size? resize = null, ImageFormat format = null)
        {
            Func<Rectangle, TimeSpan, Size?, ImageFormat, Screenshot> getScreenshot = GetScreenshot;
            
            return getScreenshot.BeginInvoke(region, timeout, resize, format ?? ImageFormat.Bmp, callback, getScreenshot);
        }

        public Screenshot EndGetScreenshot(IAsyncResult result)
        {
            Func<Rectangle, TimeSpan, Size?, ImageFormat, Screenshot> getScreenshot = result.AsyncState as Func<Rectangle, TimeSpan, Size?, ImageFormat, Screenshot>;
            if (getScreenshot != null)
            {
                return getScreenshot.EndInvoke(result);
            }
            else
                return null;
        }

        public void SendScreenshotResponse(Screenshot screenshot)
        {
            if (_screenshot_request_id != null && screenshot != null && screenshot.RequestId == _screenshot_request_id.Value)
            {
                if (_screenshot_action != null)
                {
                    _screenshot_action(screenshot);
                }
            }
        }

        public void Message(string message)
        {
            SafeInvokeMessageRecevied(message);
        }

        public void Ping()
        {

        }

        public void Disconnect()
        {
            SafeInvokeDisconnected();
        }

        // Message handlers
        private void SafeInvokeRecordingStarted()
        {
            if (RecordingStarted == null)
                return; // no one's listening

            RecordingStartedEvent listener = null;
            Delegate[] dels = RecordingStarted.GetInvocationList();

            foreach (Delegate del in dels)
            {
                try
                {
                    listener = (RecordingStartedEvent)del;
                    listener.Invoke();
                }
                catch (Exception)
                {
                    RecordingStarted -= listener;
                }
            }
        }

        private void SafeInvokeRecordingStopped()
        {
            if (RecordingStopped == null)
                return; // no one's listening

            RecordingStoppedEvent listener = null;
            Delegate[] dels = RecordingStopped.GetInvocationList();

            foreach (Delegate del in dels)
            {
                try
                {
                    listener = (RecordingStoppedEvent)del;
                    listener.Invoke();
                }
                catch (Exception)
                {
                    RecordingStopped -= listener;
                }
            }
        }

        private void SafeInvokeMessageRecevied(string message)
        {
            if (RemoteMessage == null)
                return; // no one's listening

            MessageReceivedEvent listener = null;
            Delegate[] dels = RemoteMessage.GetInvocationList();

            foreach (Delegate del in dels)
            {
                try
                {
                    listener = (MessageReceivedEvent)del;
                    listener.Invoke(message);
                }
                catch (Exception)
                {
                    RemoteMessage -= listener;
                }
            }
        }

        private void SafeInvokeScreenshotRequested()
        {
            if (ScreenshotRequested == null)
            {
                Message("SaveInvokeScreenshotRequested: Bailing, no one's listening");
                return; // no one's listening
            }

            ScreenshotRequestedEvent listener = null;
            Delegate[] dels = ScreenshotRequested.GetInvocationList();

            foreach (Delegate del in dels)
            {
                try
                {
                    listener = (ScreenshotRequestedEvent)del;
                    listener.Invoke();
                }
                catch (Exception)
                {
                    ScreenshotRequested -= listener;
                }
            }
        }

        private void SafeInvokeScreenshotReceived(Screenshot screenshot)
        {
            if (ScreenshotReceived == null)
                return; // no one's listening

            ScreenshotReceivedEvent listener = null;
            Delegate[] dels = ScreenshotReceived.GetInvocationList();

            foreach (Delegate del in dels)
            {
                try
                {
                    listener = (ScreenshotReceivedEvent)del;
                    listener.Invoke(screenshot);
                }
                catch (Exception)
                {
                    ScreenshotReceived -= listener;
                }
            }
        }

        private void SafeInvokeDisconnected()
        {
            if (Disconnected == null)
                return; // no one's listening

            DisconnectedEvent listener = null;
            Delegate[] dels = Disconnected.GetInvocationList();

            foreach (Delegate del in dels)
            {
                try
                {
                    listener = (DisconnectedEvent)del;
                    listener.Invoke();
                }
                catch (Exception)
                {
                    Disconnected -= listener;
                }
            }
        }

        // Client proxy for marshalling the handlers
        public class ClientCaptureInterfaceEventProxy : MarshalByRefObject
        {
            #region Event Declarations

            /// <summary>
            /// Client event used to communicate to the client that it is time to start recording
            /// </summary>
            public event RecordingStartedEvent RecordingStarted;

            /// <summary>
            /// Client event used to communicate to the client that it is time to stop recording
            /// </summary>
            public event RecordingStoppedEvent RecordingStopped;

            /// <summary>
            /// Client event used to communicate to the client that it is time to create a screenshot
            /// </summary>
            public event ScreenshotRequestedEvent ScreenshotRequested;

            /// <summary>
            /// Client event used to notify the hook to exit
            /// </summary>
            public event DisconnectedEvent Disconnected;

            #endregion

            #region Lifetime Services

            public override object InitializeLifetimeService()
            {
                //Returning null holds the object alive until it is explicitly destroyed
                return null;
            }

            #endregion

            public void RecordingStartedProxyHandler()
            {
                if (RecordingStarted != null)
                    RecordingStarted();
            }

            public void RecordingStoppedProxyHandler()
            {
                if (RecordingStopped != null)
                    RecordingStopped();
            }


            public void DisconnectedProxyHandler()
            {
                if (Disconnected != null)
                    Disconnected();
            }

            public void ScreenshotRequestedProxyHandler()
            {
                if (ScreenshotRequested != null)
                    ScreenshotRequested();
            }
        }

    }
}
