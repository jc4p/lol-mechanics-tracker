using EasyHook;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Remoting.Channels.Ipc;
using System.Text;
using System.Threading.Tasks;

namespace Direct3DCapture
{
    public class CaptureProcess : IDisposable
    {
        string ChannelName = null;
        private IpcServerChannel Gateway;
        public CaptureInterface Interface { get; private set; }
        public Process Process { get; set; }

        public CaptureProcess(Process process, CaptureInterface captureInterface)
        {
            captureInterface.ProcessId = process.Id;
            Gateway = RemoteHooking.IpcCreateServer(ref ChannelName, System.Runtime.Remoting.WellKnownObjectMode.Singleton, Interface);
            Interface = captureInterface;
            
            try
            {
                RemoteHooking.Inject(process.Id, InjectionOptions.DoNotRequireStrongName, typeof(CaptureInterface).Assembly.Location, typeof(CaptureInterface).Assembly.Location, ChannelName);
                Interface.Message("Injected into process");
            }
            catch(Exception e)
            {
                throw new Exception("Couldn't inject into process");
            }

            Process = process;
        }

        private bool _disposed = false;
        ~CaptureProcess()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Disconnect the IPC (which causes the remote entry point to exit)
                    Interface.Disconnect();
                }

                _disposed = true;
            }
        }
    }
}
