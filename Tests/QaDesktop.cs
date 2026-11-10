using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Test harness only. A private Windows desktop is never made visible or activated.
// This also contains engine/driver windows created before the mod can run.
public sealed class ProbablyCoopQaDesktop : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct StartupInfo { public int cb; public string reserved,desktop,title; public int x,y,xSize,ySize,xCount,yCount,fill,flags; public short show,reserved2; public IntPtr reservedPtr,input,output,error; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr process,thread; public int processId,threadId; }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr CreateDesktopW(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool CloseDesktop(IntPtr desktop);
    private delegate bool WindowVisitor(IntPtr window,IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumDesktopWindows(IntPtr desktop,WindowVisitor visitor,IntPtr unused);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window,System.Text.StringBuilder title,int capacity);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateProcessW(string application,System.Text.StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string cwd,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private IntPtr handle;
    public string Name { get; private set; }
    public ProbablyCoopQaDesktop()
    {
        Name="ProbablyCoopQA-"+Guid.NewGuid().ToString("N");
        handle=CreateDesktopW(Name,IntPtr.Zero,IntPtr.Zero,0,0x01ff,IntPtr.Zero);
        if(handle==IntPtr.Zero) throw new Win32Exception();
    }
    public string GameWindowTitle(int pid)
    {
        string title=null;
        EnumDesktopWindows(handle,delegate(IntPtr window,IntPtr unused) {
            uint process;
            GetWindowThreadProcessId(window,out process);
            if(process!=(uint)pid || !IsWindowVisible(window)) return true;
            var buffer=new System.Text.StringBuilder(256);
            GetWindowTextW(window,buffer,buffer.Capacity);
            string candidate=buffer.ToString();
            if(!candidate.StartsWith("Probably Stolen",StringComparison.Ordinal)) return true;
            title=candidate;
            return false;
        },IntPtr.Zero);
        return title;
    }
    public Process Start(string executable,string arguments,string cwd,bool windowed=false)
    {
        if(handle==IntPtr.Zero) throw new ObjectDisposedException("QA desktop");
        var si=new StartupInfo { cb=Marshal.SizeOf(typeof(StartupInfo)),desktop="WinSta0\\"+Name,flags=1,show=(short)(windowed ? 1 : 0) };
        ProcessInfo pi;
        var command=new System.Text.StringBuilder("\""+executable+"\" "+arguments);
        if(!CreateProcessW(executable,command,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,cwd,ref si,out pi)) throw new Win32Exception();
        try { var result=Process.GetProcessById(pi.processId); result.EnableRaisingEvents=true; return result; }
        finally { CloseHandle(pi.thread); CloseHandle(pi.process); }
    }
    public void Dispose() { if(handle!=IntPtr.Zero) { CloseDesktop(handle);handle=IntPtr.Zero; } }
}
