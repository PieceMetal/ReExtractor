using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
namespace ReExtractor.Gui;
public static class FeedbackScreenshot
{
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    public static byte[]? ReadPng()
    {
        if (!OperatingSystem.IsWindows() || !OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var png=Read(RegisterClipboardFormat("PNG"));
            if(png!=null)return png;
            var dib=Read(8); if(dib==null||dib.Length<40)return null;
            int header=BitConverter.ToInt32(dib,0), width=BitConverter.ToInt32(dib,4), height=BitConverter.ToInt32(dib,8);
            int bits=BitConverter.ToUInt16(dib,14),compression=BitConverter.ToInt32(dib,16);
            if(header<40||header>dib.Length||width<=0||height==int.MinValue||Math.Abs((long)height)*width>30000000||(bits!=24&&bits!=32)||(compression!=0&&compression!=3))return null;
            var offset=14+header+(header==40&&compression==3?12:0);
            using var bmp=new MemoryStream();
            using(var writer=new BinaryWriter(bmp,System.Text.Encoding.UTF8,true)){writer.Write((ushort)0x4d42);writer.Write(dib.Length+14);writer.Write(0);writer.Write(offset);writer.Write(dib);}
            bmp.Position=0;using var bitmap=new Bitmap(bmp);using var output=new MemoryStream();bitmap.Save(output);return output.ToArray();
        }
        finally { CloseClipboard(); }
    }
    private static byte[]? Read(uint format)
    {
        var handle=GetClipboardData(format);if(handle==IntPtr.Zero)return null;
        var size=GlobalSize(handle).ToUInt64();if(size==0||size>128*1024*1024)return null;
        var pointer=GlobalLock(handle);if(pointer==IntPtr.Zero)return null;
        try{var bytes=new byte[(int)size];Marshal.Copy(pointer,bytes,0,bytes.Length);return bytes;}
        finally{GlobalUnlock(handle);}
    }
}
