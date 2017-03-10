using EasyHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LolTracker.Capture
{
    class HookInfo<T> : HookInfo
    {
        public T Original { get; private set; }

        public HookInfo(IntPtr func, Delegate newProc, object owner) : base(func, newProc, owner)
        {
            Original = (T)(object)Marshal.GetDelegateForFunctionPointer<T>(func);
        }
    }

    class HookInfo
    {
        private readonly IntPtr func;
        private readonly Delegate newProc;
        private readonly object callback;
        public LocalHook Hook { get; private set; }
        private bool IsHooked { get; set; }

        public HookInfo(IntPtr func, Delegate newProc, object owner)
        {
            this.func = func;
            this.newProc = newProc;
            callback = owner;
        }

        public void Create()
        {
            if (Hook == null)
            {
                Hook = LocalHook.Create(func, newProc, callback);
                IsHooked = true;
                Hook.ThreadACL.SetExclusiveACL(new [] { 0 });
            }
        }
    }
}
