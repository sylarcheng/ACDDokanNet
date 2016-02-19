﻿using DokanNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.AccessControl;
using FileAccess = DokanNet.FileAccess;
using Azi.Tools;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Security.Principal;
using ShellExtension;

namespace Azi.ACDDokanNet
{
    internal class VirtualDrive : IDokanOperations
    {
        private string creator = WindowsIdentity.GetCurrent().Name;
        private FSProvider provider;

        public VirtualDrive(FSProvider provider)
        {
            this.provider = provider;
        }

        public void Cleanup(string fileName, DokanFileInfo info)
        {
            try
            {
                if (info.Context != null)
                {
                    var str = info.Context as IBlockStream;
                    if (str != null)
                    {
                        str.Close();
                    }
                }

                if (info.DeleteOnClose)
                {
                    if (info.IsDirectory)
                    {
                        provider.DeleteDir(fileName);
                    }
                    else
                    {
                        provider.DeleteFile(fileName);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        public void CloseFile(string fileName, DokanFileInfo info)
        {
            try
            {
                if (info.Context != null)
                {
                    var str = info.Context as IBlockStream;
                    if (str != null)
                    {
                        str.Close();
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private NtStatus _CreateDirectory(string fileName, DokanFileInfo info)
        {
            if (ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            if (provider.Exists(fileName))
            {
                return DokanResult.AlreadyExists;
            }

            provider.CreateDir(fileName);
            return DokanResult.Success;
        }

        private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                      FileAccess.Execute |
                                      FileAccess.GenericExecute | FileAccess.GenericWrite | FileAccess.GenericRead;

        private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData |
                                                   FileAccess.Delete |
                                                   FileAccess.GenericWrite;

        private const FileAccess DataReadAccess = FileAccess.ReadData | FileAccess.GenericExecute |
                                                   FileAccess.Execute;

#if TRACE
        string lastFilePath;
#endif

        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, DokanFileInfo info)
        {
            try
            {
                var res = MainCreateFile(fileName, access, share, mode, options, attributes, info);
#if TRACE
                bool readWriteAttributes = (access & DataAccess) == 0;
                if (!(readWriteAttributes || info.IsDirectory) || (res != DokanResult.Success && !(lastFilePath == fileName && res == DokanResult.FileNotFound)))
                {
                    if (!(info.Context is NewFileBlockWriter || info.Context is FileBlockReader || info.Context is SmallFileBlockReaderWriter || info.Context is BufferedAmazonBlockReader))
                        Log.Trace($"{fileName}\r\n  Access:[{access}]\r\n  Share:[{share}]\r\n  Mode:[{mode}]\r\n  Options:[{options}]\r\n  Attr:[{attributes}]\r\nStatus:{res}");
                    lastFilePath = fileName;
                }
#endif
                return res;
            }
            catch (Exception e)
            {
                Log.Error($"{fileName}\r\n  Access:[{access}]\r\n  Share:[{share}]\r\n  Mode:[{mode}]\r\n  Options:[{options}]\r\n  Attr:[{attributes}]\r\n\r\n{e}");
                return DokanResult.Error;
            }
        }

        public NtStatus MainCreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, DokanFileInfo info)
        {
            if (!HasAccess(info))
            {
                return DokanResult.AccessDenied;
            }

            if (info.IsDirectory)
            {
                if (mode == FileMode.CreateNew)
                {
                    return _CreateDirectory(fileName, info);
                }

                if (mode == FileMode.Open && !provider.Exists(fileName))
                {
                    return DokanResult.PathNotFound;
                }

                if (access == FileAccess.Synchronize)
                {
                    info.Context = new object();
                    return DokanResult.Success;
                }

                info.Context = new object();
                return DokanResult.Success;
            }

            string streamName = null;
            if (fileName.Contains(':'))
            {
                var names = fileName.Split(':');
                fileName = names[0];
                streamName = names[1];
            }

            var item = provider.GetItem(fileName);

            if (streamName != null && item != null)
            {
                Log.Trace($"Opening alternate stream {fileName}:{streamName}");
                if (mode != FileMode.Open)
                {
                    return DokanResult.AccessDenied;
                }

                switch (streamName)
                {
                    case ContextMenu.ACDDokanNetInfoStreamName:
                        if (item.Info == null)
                        {
                            provider.BuildItemInfo(item);
                        }

                        return OpenAsByteArray(item.Info, info);
                }

                return DokanResult.AccessDenied;
            }

            bool readWriteAttributes = (access & DataAccess) == 0;
            switch (mode)
            {
                case FileMode.Open:
                    if (item == null)
                    {
                        return DokanResult.FileNotFound;
                    }

                    if (item.IsDir)
                    // check if driver only wants to read attributes, security info, or open directory
                    {
                        info.IsDirectory = item.IsDir;
                        info.Context = new object();
                        // must set it to something if you return DokanError.Success

                        return DokanResult.Success;
                    }

                    break;

                case FileMode.CreateNew:
                    if (item != null)
                    {
                        return DokanResult.FileExists;
                    }

                    break;

                case FileMode.Truncate:
                    if (item == null)
                    {
                        return DokanResult.FileNotFound;
                    }

                    break;
            }

            if (readWriteAttributes)
            {
                info.Context = new object();
                return DokanResult.Success;
            }

            return _OpenFile(fileName, access, share, mode, options, attributes, info);
        }

        private NtStatus OpenAsByteArray(byte[] data, DokanFileInfo info)
        {
            info.Context = new ByteArrayBlockReader(data);
            return DokanResult.Success;
        }

        private bool HasAccess(DokanFileInfo info)
        {
            var watch = Stopwatch.StartNew();
            var identity = Processes.GetProcessOwner(info.ProcessId);
            if (identity == "NT AUTHORITY\\SYSTEM" || identity == creator)
            {
                return true;
            }

            Log.Warn($"User {identity} has no access to Amazon Cloud Drive on drive {MountPath}\r\nCreator User is {creator} - Identity User is {identity}");
            return false;
        }

        private NtStatus _OpenFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, DokanFileInfo info)
        {
            bool readAccess = (access & DataReadAccess) != 0;
            bool writeAccess = (access & DataWriteAccess) != 0;

            if (writeAccess && ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            System.IO.FileAccess IOaccess = System.IO.FileAccess.Read;
            if (!readAccess && writeAccess)
            {
                IOaccess = System.IO.FileAccess.Write;
            }

            if (readAccess && writeAccess)
            {
                IOaccess = System.IO.FileAccess.ReadWrite;
            }

            var result = provider.OpenFile(fileName, mode, IOaccess, share, options);

            if (result == null)
            {
                return DokanResult.AccessDenied;
            }

            info.Context = result;
            return DokanResult.Success;
        }

        public NtStatus DeleteDirectory(string fileName, DokanFileInfo info)
        {
            if (ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            if (!provider.Exists(fileName))
            {
                return DokanResult.PathNotFound;
            }

            provider.DeleteDir(fileName);
            return DokanResult.Success;
        }

        public NtStatus DeleteFile(string fileName, DokanFileInfo info)
        {
            if (ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            if (!provider.Exists(fileName))
            {
                return DokanResult.PathNotFound;
            }

            Log.Trace("Delete file:" + fileName);

            provider.DeleteFile(fileName);
            return DokanResult.Success;
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, DokanFileInfo info)
        {
            if (!HasAccess(info))
            {
                files = null;
                return DokanResult.AccessDenied;
            }

            try
            {
                var items = provider.GetDirItems(fileName).Result;

                files = items.Select(i => new FileInformation
                {
                    Length = i.Length,
                    FileName = i.Name,
                    Attributes = i.IsDir ? FileAttributes.Directory : FileAttributes.Normal,
                    LastAccessTime = i.LastAccessTime,
                    LastWriteTime = i.LastWriteTime,
                    CreationTime = i.CreationTime
                }).ToList();
                return DokanResult.Success;
            }
            catch (Exception e)
            {
                Log.Error(e);
                files = new List<FileInformation>();
                return DokanResult.Error;
            }
        }

        public NtStatus FlushFileBuffers(string fileName, DokanFileInfo info)
        {
            try
            {
                if (info.Context != null)
                {
                    (info.Context as IBlockStream)?.Flush();
                }

                return DokanResult.Success;
            }
            catch (Exception e)
            {
                Log.Error(e);
                return DokanResult.Error;
            }
        }

        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, DokanFileInfo info)
        {
            try
            {
                freeBytesAvailable = provider.TotalSize - provider.TotalUsedSpace;
                totalNumberOfBytes = provider.TotalSize;
                totalNumberOfFreeBytes = provider.TotalSize - provider.TotalUsedSpace;

                return DokanResult.Success;
            }
            catch (Exception e)
            {
                freeBytesAvailable = 0;
                totalNumberOfBytes = 0;
                totalNumberOfFreeBytes = 0;
                Log.Error(e);
                return DokanResult.Error;
            }
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, DokanFileInfo info)
        {
            if (!HasAccess(info))
            {
                fileInfo = default(FileInformation);
                return DokanResult.AccessDenied;
            }

            try
            {
                var item = provider.GetItem(fileName);

                if (item != null)
                {
                    fileInfo = MakeFileInformation(item);
                    return DokanResult.Success;
                }

                fileInfo = default(FileInformation);
                return DokanResult.PathNotFound;
            }
            catch (Exception e)
            {
                Log.Error(e);
                fileInfo = default(FileInformation);
                return DokanResult.Error;
            }
        }

        private FileInformation MakeFileInformation(FSItem i)
        {
            var result = new FileInformation
            {
                Length = i.Length,
                FileName = i.Name,
                Attributes = i.IsDir ? FileAttributes.Directory : FileAttributes.Normal,
                LastAccessTime = i.LastAccessTime,
                LastWriteTime = i.LastWriteTime,
                CreationTime = i.CreationTime
            };
            if (ReadOnly)
            {
                result.Attributes |= FileAttributes.ReadOnly;
            }

            return result;
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, DokanFileInfo info)

        {
            Log.Trace(fileName);

            var identity = WindowsIdentity.GetCurrent().Owner;
            FileSystemSecurity result = info.IsDirectory
                ? (FileSystemSecurity)new DirectorySecurity()
                : (FileSystemSecurity)new FileSecurity();
            if (sections.HasFlag(AccessControlSections.Access))
            {
                result.SetAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
                result.SetAccessRuleProtection(false, true);
            }

            if (sections.HasFlag(AccessControlSections.Owner))
            {
                result.SetOwner(identity);
            }

            security = result;
            return DokanResult.Success;
        }

        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, DokanFileInfo info)
        {
            volumeLabel = provider.VolumeName;
            features =
                FileSystemFeatures.NamedStreams |
                FileSystemFeatures.SupportsRemoteStorage |
                FileSystemFeatures.CasePreservedNames |
                FileSystemFeatures.CaseSensitiveSearch |
                FileSystemFeatures.SupportsRemoteStorage |
                FileSystemFeatures.UnicodeOnDisk |
                FileSystemFeatures.SequentialWriteOnce;
            if (ReadOnly)
            {
                features |= FileSystemFeatures.ReadOnlyVolume;
            }

            fileSystemName = provider.FileSystemName;
            return DokanResult.Success;
        }

        public NtStatus LockFile(string fileName, long offset, long length, DokanFileInfo info)
        {
            try
            {
                return DokanResult.Success;
            }
            catch (Exception e)
            {
                Log.Error(e);
                return DokanResult.Error;
            }
        }

        public NtStatus MoveFile(string oldName, string newName, bool replace, DokanFileInfo info)
        {
            if (!HasAccess(info))
            {
                return DokanResult.AccessDenied;
            }

            if (ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            try
            {
                provider.MoveFile(oldName, newName, replace);
                Log.Trace("Move file:" + oldName + " - " + newName);

                return DokanResult.Success;
            }
            catch (Exception e)
            {
                Log.Error(e);
                return DokanResult.Error;
            }
        }

        private const int ReadTimeout = 30000;

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, DokanFileInfo info)
        {
            var start = DateTime.UtcNow;
            try
            {
                var reader = info.Context as IBlockStream;

                bytesRead = reader.Read(offset, buffer, 0, buffer.Length, ReadTimeout);
                return DokanResult.Success;
            }
            catch (ObjectDisposedException)
            {
                bytesRead = 0;
                return NtStatus.FileClosed;
            }
            catch (NotSupportedException)
            {
                Log.Warn("ReadWrite not supported: " + fileName);
                bytesRead = 0;
                return DokanResult.AccessDenied;
            }
            catch (TimeoutException)
            {
                Log.Warn("Timeout " + (DateTime.UtcNow - start).TotalMilliseconds);
                bytesRead = 0;
                return NtStatus.Timeout;
            }
            catch (Exception e)
            {
                Log.Error(e);
                bytesRead = 0;
                return DokanResult.Error;
            }
        }

        public NtStatus SetAllocationSize(string fileName, long length, DokanFileInfo info)
        {
            if (ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            // Log.Trace(fileName);
            return DokanResult.Success;
        }

        public NtStatus SetEndOfFile(string fileName, long length, DokanFileInfo info)
        {
            if (ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            // Log.Trace(fileName);

            var file = info.Context as IBlockStream;
            file.SetLength(length);
            Log.Trace($"{fileName} to {length}");

            return DokanResult.Success;
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, DokanFileInfo info)
        {
            if (ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            Log.Trace(fileName);
            return DokanResult.Error;
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, DokanFileInfo info)
        {
            if (ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            Log.Trace(fileName);
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, DokanFileInfo info)
        {
            if (ReadOnly)
            {
                return DokanResult.AccessDenied;
            }

            Log.Trace(fileName);
            return DokanResult.Error;
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, DokanFileInfo info)
        {
            Log.Trace(fileName);
            return DokanResult.Success;
        }

        public NtStatus Unmount(DokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, DokanFileInfo info)
        {
            if (ReadOnly)
            {
                bytesWritten = 0;
                return DokanResult.AccessDenied;
            }

            try
            {
                if (info.Context != null)
                {
                    var writer = info.Context as IBlockStream;
                    if (writer != null)
                    {
                        writer.Write(offset, buffer, 0, buffer.Length);
                        bytesWritten = buffer.Length;
                        return DokanResult.Success;
                    }
                }

                bytesWritten = 0;
                return DokanResult.AccessDenied;
            }
            catch (NotSupportedException)
            {
                Log.Warn("ReadWrite not supported: " + fileName);
                bytesWritten = 0;
                return DokanResult.AccessDenied;
            }
            catch (Exception e)
            {
                Log.Error(e);
                bytesWritten = 0;
                return DokanResult.AccessDenied;
            }
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, DokanFileInfo info)
        {
            Log.Trace(fileName);
            streams = new List<FileInformation>();
            try
            {

                var item = provider.GetItem(fileName);
                if (item == null)
                {
                    return DokanResult.FileNotFound;
                }

                if (!item.IsDir)
                {
                    var infostream = MakeFileInformation(item);
                    infostream.FileName = "::$DATA";
                    streams.Add(infostream);
                }

                {
                    var infostream = new FileInformation
                    {
                        FileName = $":{ContextMenu.ACDDokanNetInfoStreamName}:$DATA",
                        Length = 2
                    };
                    streams.Add(infostream);
                }

                return DokanResult.Success;
            }
            catch (Exception e)
            {
                Log.Error(e);
                return DokanResult.Error;
            }

        }

        public Action OnMount;
        internal Action OnUnmount;
        internal string MountPath;

        public bool ReadOnly { get; internal set; }

        public NtStatus Mounted(DokanFileInfo info)
        {
            OnMount?.Invoke();
            return DokanResult.Success;
        }

        public NtStatus Unmounted(DokanFileInfo info)
        {
            OnUnmount?.Invoke();
            return DokanResult.Success;
        }
    }
}
