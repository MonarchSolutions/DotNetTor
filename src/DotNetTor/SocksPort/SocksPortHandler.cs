﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetTor.SocksPort
{

	public sealed class SocksPortHandler : HttpMessageHandler
	{
		// Tolerate errors
		private const int MaxRetry = 3;
		private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

		public bool IgnoreSslCertification { get; set; }

		private static ConcurrentDictionary<string, SocksConnection> _Connections = new ConcurrentDictionary<string, SocksConnection>();

		#region Constructors

		public SocksPortHandler(string address = "127.0.0.1", int socksPort = 9050, bool ignoreSslCertification = false)
			: this(new IPEndPoint(IPAddress.Parse(address), socksPort), ignoreSslCertification)
		{

		}

		public SocksPortHandler(IPEndPoint endpoint, bool ignoreSslCertification = false)
		{
			if (EndPoint == null)
				EndPoint = endpoint;
			else if (!Equals(EndPoint.Address, endpoint.Address) || !Equals(EndPoint.Port, endpoint.Port))
			{
				throw new TorException($"Cannot change {nameof(endpoint)}, until every {nameof(SocksPortHandler)}, is disposed. " +
										$"The current {nameof(endpoint)} is {EndPoint.Address}:{EndPoint.Port}, your desired is {endpoint.Address}:{endpoint.Port}");
			}
			IgnoreSslCertification = ignoreSslCertification;
		}


		public readonly IPEndPoint EndPoint = null;
		#endregion

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			await Util.Semaphore.WaitAsync().ConfigureAwait(false);

			try
			{
				return Retry.Do(() => Send(request), RetryInterval, MaxRetry);
			}
			catch (Exception ex)
			{
				throw new TorException("Couldn't send the request", ex);
			}
			finally
			{
				Util.Semaphore.Release();
			}
		}

		private HttpResponseMessage Send(HttpRequestMessage request)
		{
			SocksConnection connection = null;
			try
			{
				Retry.Do(() =>
				{
					connection = ConnectToDestinationIfNotConnected(request.RequestUri);
				}, RetryInterval, MaxRetry);
			}
			catch (Exception ex)
			{
				throw new TorException("Failed to connect to the destination", ex);
			}

			Util.ValidateRequest(request);
			HttpResponseMessage message = connection.SendRequest(request, IgnoreSslCertification);

			return message;
		}



		#region DestinationConnections

		private List<Uri> _References = new List<Uri>();
		private SocksConnection ConnectToDestinationIfNotConnected(Uri uri)
		{
			uri = Util.StripPath(uri);
			lock (_Connections)
			{
				if (_Connections.TryGetValue(uri.AbsoluteUri, out SocksConnection connection))
				{
					if (!_References.Contains(uri))
					{
						connection.AddReference();
						_References.Add(uri);
					}
					return connection;
				}

				connection = new SocksConnection
				{
					EndPoint = EndPoint,
					Destination = uri
				};
				connection.AddReference();
				_References.Add(uri);
				_Connections.TryAdd(uri.AbsoluteUri, connection);
				return connection;
			}
		}

		#endregion

		#region TorConnection


		#endregion

		#region Cleanup

		private void ReleaseUnmanagedResources()
		{
			lock (_Connections)
			{
				foreach (var reference in _References)
				{
					if (_Connections.TryGetValue(reference.AbsoluteUri, out SocksConnection connection))
					{
						connection.RemoveReference(out bool disposedSockets);
						if (disposedSockets)
						{
							_Connections.TryRemove(reference.AbsoluteUri, out connection);
						}
					}
				}
			}
		}

		private volatile bool _disposed = false;
		protected override void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				try
				{
					ReleaseUnmanagedResources();
				}
				catch (Exception)
				{
					// ignored
				}

				_disposed = true;
			}

			base.Dispose(disposing);
		}
		~SocksPortHandler()
		{
			Dispose(false);
		}

		#endregion
	}
}
