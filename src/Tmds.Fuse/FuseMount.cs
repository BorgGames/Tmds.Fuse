using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tmds.Fuse
{
    [System.Serializable]
    public class FuseException : System.Exception
    {
        public FuseException() { }
        public FuseException(string message) : base(message) { }
        public FuseException(string message, System.Exception inner) : base(message, inner) { }
        protected FuseException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    class FuseMount
    {
        private readonly string _mountPoint;
        private readonly IFuseFileSystem _fileSystem;
        private readonly getattr_Delegate _getattr;
        private readonly readdir_Delegate _readdir;
        private readonly open_Delegate _open;
        private readonly read_Delegate _read;
        private unsafe class ManagedFiller
        {
            public readonly fuse_fill_dir* Filler;
            public readonly fuse_fill_dir_Delegate Delegate;

            public ManagedFiller(fuse_fill_dir* filler, fuse_fill_dir_Delegate fillDelegate)
            {
                Filler = filler;
                Delegate = fillDelegate;
            }
        }
        private ManagedFiller _previousFiller;

        public unsafe FuseMount(string mountPoint, IFuseFileSystem fileSystem)
        {
            _mountPoint = mountPoint;
            _fileSystem = fileSystem;
            _getattr = Getattr;
            _read = Read;
            _open = Open;
            _readdir = Readdir;
        }

        private unsafe int Getattr(path* path, stat* stat, fuse_file_info* fi)
        {
            Stat s = ToStat(stat);
            s.Clear();
            return _fileSystem.GetAttr(ToSpan(path), s, ToFileInfo(fi));
        }

        private unsafe int Readdir(path* path, void* buf, fuse_fill_dir* filler, ulong offset, fuse_file_info* fi, int flags)
        {
            // try to reuse the previous delegate
            fuse_fill_dir_Delegate fillDelegate;
            ManagedFiller previousFiller = _previousFiller;
            if (previousFiller != null && previousFiller.Filler == filler)
            {
                fillDelegate = previousFiller.Delegate;
            }
            else
            {
                fillDelegate = Marshal.GetDelegateForFunctionPointer<fuse_fill_dir_Delegate>(new IntPtr(filler));
                _previousFiller = new ManagedFiller(filler, fillDelegate);
            }

            return _fileSystem.ReadDir(ToSpan(path), offset, (ReadDirFlags)flags, ToDirectoryContent(buf, fillDelegate), ToFileInfo(fi));
        }

        private unsafe int Open(path* path, fuse_file_info* fi)
        {
            return _fileSystem.Open(ToSpan(path), ToFileInfo(fi));
        }

        private unsafe int Read(path* path, void* buffer, UIntPtr size, ulong off, fuse_file_info* fi)
        {
            // TODO: handle size > int.MaxValue
            return _fileSystem.Read(ToSpan(path), off, new Span<byte>(buffer, (int)size), ToFileInfo(fi));
        }

        private unsafe FileInfo ToFileInfo(fuse_file_info* fi) => new FileInfo(fi);

        private unsafe Stat ToStat(stat* stat) => new Stat(stat);

        private unsafe ReadOnlySpan<byte> ToSpan(path* path)
        {
            var span = new ReadOnlySpan<byte>(path, int.MaxValue);
            return span.Slice(0, span.IndexOf((byte)0));
        }

        private unsafe DirectoryContent ToDirectoryContent(void* buffer, fuse_fill_dir_Delegate fillDelegate) => new DirectoryContent(buffer, fillDelegate);

        public unsafe void Mount()
        {
            // TODO: delete args
            fuse_args args;
            LibFuse.fuse_opt_add_arg(&args, "");

            fuse_operations ops;
            ops.getattr = Marshal.GetFunctionPointerForDelegate(_getattr);
            ops.readdir = Marshal.GetFunctionPointerForDelegate(_readdir);
            ops.open = Marshal.GetFunctionPointerForDelegate(_open);
            ops.read = Marshal.GetFunctionPointerForDelegate(_read);

            // TODO: cleanup/unmount
            var fuse = LibFuse.fuse_new(&args, &ops, (UIntPtr)sizeof(fuse_operations), null);
            int rv = LibFuse.fuse_mount(fuse, _mountPoint);
            if (rv != 0)
            {
                ThrowException(nameof(LibFuse.fuse_mount), rv);
            }
            rv = LibFuse.fuse_loop(fuse);
            if (rv != 0)
            {
                ThrowException(nameof(LibFuse.fuse_loop), rv);
            }
        }

        private void ThrowException(string operation, int returnValue)
        {
            throw new FuseException($"Failed to {operation}, the function returned {returnValue}.");
        }
    }
}