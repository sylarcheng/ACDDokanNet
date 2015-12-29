﻿using Azi.Amazon.CloudDrive;
using Azi.Amazon.CloudDrive.JsonObjects;
using Azi.Tools;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tools;

namespace Azi.ACDDokanNet
{

    public class NodeTreeCache : IDisposable
    {
        class DirItem
        {
            public readonly DateTime ExpirationTime;
            public readonly HashSet<string> Items;
            public DirItem(IList<string> items, int expirationSeconds)
            {
                Items = new HashSet<string>(items);
                ExpirationTime = DateTime.UtcNow.AddSeconds(expirationSeconds);
            }

            public bool IsExpired => DateTime.UtcNow > ExpirationTime;
        }

        private readonly ReaderWriterLockSlim lok = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly Dictionary<string, FSItem> pathToNode = new Dictionary<string, FSItem>();
        private readonly Dictionary<string, DirItem> pathToDirItem = new Dictionary<string, DirItem>();
        public int DirItemsExpirationSeconds = 60;
        public int FSItemsExpirationSeconds = 5 * 60;


        public FSItem GetNode(string filePath)
        {
            lok.EnterUpgradeableReadLock();
            FSItem item;
            try
            {
                if (!pathToNode.TryGetValue(filePath, out item)) return null;
                if (item.IsExpired(FSItemsExpirationSeconds))
                {
                    lok.EnterWriteLock();
                    try
                    {
                        pathToNode.Remove(filePath);
                        return null;
                    }
                    finally
                    {
                        lok.ExitWriteLock();
                    }
                }
                return item;
            }
            finally
            {
                lok.ExitUpgradeableReadLock();
            }
        }
        public IEnumerable<string> GetDir(string filePath)
        {
            DirItem item;
            lok.EnterUpgradeableReadLock();
            try
            {
                if (!pathToDirItem.TryGetValue(filePath, out item)) return null;
                if (!item.IsExpired) return item.Items;
                lok.EnterWriteLock();
                try
                {
                    pathToDirItem.Remove(filePath);
                    return null;
                }
                finally
                {
                    lok.ExitWriteLock();
                }
            }
            finally
            {
                lok.ExitUpgradeableReadLock();
            }

        }

        public void Add(FSItem item)
        {
            lok.EnterWriteLock();
            try
            {
                pathToNode[item.Path] = item;
                DirItem dirItem;
                if (pathToDirItem.TryGetValue(item.Dir, out dirItem)) dirItem.Items.Add(item.Path);
            }
            finally
            {
                lok.ExitWriteLock();
            }
        }

        public void AddDirItems(string folderPath, IList<FSItem> items)
        {
            lok.EnterWriteLock();
            try
            {
                pathToDirItem[folderPath] = new DirItem(items.Select(i => i.Path).ToList(), DirItemsExpirationSeconds);
                foreach (var item in items)
                    pathToNode[item.Path] = item;
            }
            finally
            {
                lok.ExitWriteLock();
            }
        }

        public void MoveFile(string oldPath, FSItem newNode)
        {
            lok.EnterWriteLock();
            try
            {
                DeleteFile(oldPath);
                Add(newNode);
            }
            finally
            {
                lok.ExitWriteLock();
            }
        }

        public void MoveDir(string oldPath, FSItem newNode)
        {
            lok.EnterWriteLock();
            try
            {
                DeleteDir(oldPath);
                Add(newNode);
            }
            finally
            {
                lok.ExitWriteLock();
            }
        }

        internal void Update(FSItem newitem)
        {
            lok.EnterWriteLock();
            try
            {
                if (pathToNode.ContainsKey(newitem.Path)) pathToNode[newitem.Path] = newitem;
            }
            finally
            {
                lok.ExitWriteLock();
            }
        }

        public void DeleteFile(string filePath)
        {
            lok.EnterWriteLock();
            try
            {
                var dirPath = Path.GetDirectoryName(filePath);
                DirItem dirItem;
                if (pathToDirItem.TryGetValue(dirPath, out dirItem))
                {
                    dirItem.Items.Remove(filePath);
                }
                pathToNode.Remove(filePath);
            }
            finally
            {
                //Log.Warn("File deleted: " + filePath);
                lok.ExitWriteLock();
            }
        }

        public void DeleteDir(string filePath)
        {
            lok.EnterWriteLock();
            try
            {
                var dirPath = Path.GetDirectoryName(filePath);
                DirItem dirItem;
                if (pathToDirItem.TryGetValue(dirPath, out dirItem))
                {
                    dirItem.Items.RemoveWhere(i => i == filePath);
                }

                foreach (var key in pathToNode.Keys.Where(v => v.StartsWith(filePath, StringComparison.InvariantCulture)).ToList())
                {
                    pathToNode.Remove(key);
                }

                foreach (var key in pathToDirItem.Keys.Where(v => v.StartsWith(filePath, StringComparison.InvariantCulture)).ToList())
                {
                    pathToDirItem.Remove(key);
                }
            }
            finally
            {
                lok.ExitWriteLock();
            }
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    lok.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~NodeTreeCache() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion

    }
}