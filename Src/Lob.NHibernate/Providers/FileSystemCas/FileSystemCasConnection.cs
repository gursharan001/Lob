﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lob.NHibernate.Providers.FileSystemCas
{
	public class FileSystemCasConnection : AbstractExternalBlobConnection
	{
		const string TemporaryFileBase = "$temp";
		readonly int _hashLength;
		readonly string _hashName;
		readonly string _path;

		public FileSystemCasConnection(string storagePath) : this(storagePath, null)
		{
		}

		public FileSystemCasConnection(string storagePath, string hashName)
		{
			_path = Path.GetFullPath(storagePath);
			_hashName = hashName;
			if (hashName == null)
				_hashLength = 20;
			else
				using (HashAlgorithm hash = HashAlgorithm.Create(hashName))
					_hashLength = hash.HashSize/8;
		}

		public override int BlobIdentifierLength
		{
			get { return _hashLength; }
		}

		public override Stream OpenReader(byte[] contentIdentifier)
		{
			return new FileStream(GetPath(contentIdentifier), FileMode.Open, FileAccess.Read);
		}

		public override void Delete(byte[] contentIdentifier)
		{
			string path = GetPath(contentIdentifier);
			if (File.Exists(path))
				File.Delete(path);
			DeleteFolder(contentIdentifier);
		}

		public override bool Equals(IExternalBlobConnection connection)
		{
			var c = connection as FileSystemCasConnection;
			if (c == null) return false;
			return _path.Equals(c._path) && _hashName == c._hashName;
		}

		public override void GarbageCollect(IEnumerable<byte[]> livingBlobIdentifiers)
		{
			throw new NotImplementedException();
		}

		public override ExternalBlobWriter OpenWriter()
		{
			return new FileSystemCasBlobWriter(this);
		}

		void DeleteFolder(byte[] contentIdentifier)
		{
			string path = Path.Combine(_path, contentIdentifier[0].ToString("x") + Path.DirectorySeparatorChar + contentIdentifier[1].ToString("x"));
			var d = new DirectoryInfo(path);
			try
			{
				if (d.Exists)
					d.Delete();
				d = d.Parent;
				if (d != null)
					if (d.Exists)
						d.Delete();
			}
			catch
			{
			}
		}

		void CreateFolder(byte[] contentIdentifier)
		{
			string path = Path.Combine(_path, contentIdentifier[0].ToString("x2"));
			if (!Directory.Exists(path))
				Directory.CreateDirectory(path);
			path = Path.Combine(path, contentIdentifier[1].ToString("x2"));
			if (!Directory.Exists(path))
				Directory.CreateDirectory(path);
		}

		public string GetPath(byte[] contentIdentifier)
		{
			if (contentIdentifier == null) throw new NullReferenceException("contentIdentifier cannot be null.");
			var sb = new StringBuilder();
			sb.Append(contentIdentifier[0].ToString("x2"));
			sb.Append(Path.DirectorySeparatorChar);
			sb.Append(contentIdentifier[1].ToString("x2"));
			sb.Append(Path.DirectorySeparatorChar);
			for (int i = 2; i < contentIdentifier.Length; i++)
				sb.Append(contentIdentifier[i].ToString("x2"));
			return Path.Combine(_path, sb.ToString());
		}

		#region Nested type: FileSystemCasBlobWriter

		class FileSystemCasBlobWriter : ExternalBlobWriter
		{
			FileSystemCasConnection _connection;
			HashAlgorithm _hash;
			string _tempFile;
			FileStream _tempStream;

			public FileSystemCasBlobWriter(FileSystemCasConnection connection)
			{
				if (connection == null) throw new ArgumentNullException("connection");
				_connection = connection;

				_hash = connection._hashName == null ? new SHA1CryptoServiceProvider() : HashAlgorithm.Create(connection._hashName);
				if (_hash == null) throw new Exception("Missing hash algorithm: " + connection._hashName);
				var r = new Random();
				string temp;
				int i = 0;
				do
				{
					if (i > 100) throw new Exception("Unable to find a random temporary filename for writing.");
					temp = Path.Combine(connection._path, TemporaryFileBase + r.Next().ToString("x"));
					i++;
				} while (File.Exists(temp));
				_tempStream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
				_tempFile = temp;
			}

			public override bool CanTimeout
			{
				get { return _tempStream != null ? _tempStream.CanTimeout : false; }
			}

			public override bool CanSeek
			{
				get { return _tempStream != null ? _tempStream.CanSeek : false; }
			}

			public override bool CanWrite
			{
				get { return _tempStream != null ? _tempStream.CanWrite : false; }
			}

			public override long Length
			{
				get
				{
					ThrowIfClosed();
					return _tempStream.Length;
				}
			}

			public override long Position
			{
				get
				{
					ThrowIfClosed();
					return _tempStream.Position;
				}
				set { _tempStream.Position = value; }
			}

			public override void Write(byte[] buffer, int offset, int count)
			{
				ThrowIfClosed();
				var cryptBuffer = (byte[]) buffer.Clone();
				_hash.TransformBlock(cryptBuffer, offset, count, cryptBuffer, 0);
				_tempStream.Write(buffer, offset, count);
			}

			public override void WriteByte(byte value)
			{
				ThrowIfClosed();
				var cryptBuffer = new[] {value};
				_hash.TransformBlock(cryptBuffer, 0, 1, cryptBuffer, 0);
				_tempStream.WriteByte(value);
			}

			public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
			{
				ThrowIfClosed();
				var cryptBuffer = (byte[]) buffer.Clone();
				_hash.TransformBlock(cryptBuffer, offset, count, cryptBuffer, 0);
				return _tempStream.BeginWrite(buffer, offset, count, callback, state);
			}

			public override void EndWrite(IAsyncResult asyncResult)
			{
				ThrowIfClosed();
				_tempStream.EndWrite(asyncResult);
			}

			public override byte[] Commit()
			{
				ThrowIfClosed();
				_tempStream.Flush();
				_tempStream.Dispose();

				_hash.TransformFinalBlock(new byte[0], 0, 0);

				byte[] id = _hash.Hash;
				_connection.CreateFolder(id);

				string path = _connection.GetPath(id);
				var f = new FileInfo(path);
				if (f.Exists)
				{
					var t = new FileInfo(_tempFile);
					if (f.Length != t.Length)
						throw new IOException("A file with the same hash code but a different length already exists. This is very unlikely. There might be a transfer issue.");
					t.Delete();
				}
				else
					File.Move(_tempFile, path);

				_tempStream = null;
				_tempFile = null;

				Dispose(true);
				return id;
			}

			protected override void Dispose(bool disposing)
			{
				if (_tempStream != null)
					lock (this)
					{
						_tempStream.Dispose();
						_tempStream = null;
						try
						{
							File.Delete(_tempFile);
						}
						catch
						{
						}
						_tempFile = null;
					}
				if (disposing)
				{
					if (_hash != null) ((IDisposable) _hash).Dispose();
					_hash = null;
					_connection = null;
				}
				base.Dispose(disposing);
			}

			public override void Flush()
			{
				if (_tempStream != null) _tempStream.Flush();
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				ThrowIfClosed();
				return _tempStream.Seek(offset, origin);
			}

			public override void SetLength(long value)
			{
				ThrowIfClosed();
				_tempStream.SetLength(value);
			}

			void ThrowIfClosed()
			{
				if (_tempStream == null) throw new Exception("The writer is closed.");
			}
		}

		#endregion
	}
}