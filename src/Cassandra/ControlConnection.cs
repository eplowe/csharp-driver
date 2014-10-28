//
//      Copyright (C) 2012-2014 DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Cassandra
{
    internal class ControlConnection : IDisposable
    {
        private const string SelectPeers = "SELECT peer, data_center, rack, tokens, rpc_address FROM system.peers";
        private const string SelectLocal = "SELECT * FROM system.local WHERE key='local'";
        private const CassandraEventType CassandraEventTypes = CassandraEventType.TopologyChange | CassandraEventType.StatusChange | CassandraEventType.SchemaChange;
        private static readonly IPAddress BindAllAddress = new IPAddress(new byte[4]);
        /// <summary>
        /// Protocol version used by the control connection
        /// </summary>
        private int _controlConnectionProtocolVersion = 2;

        private readonly Cluster _cluster;
        private volatile Host _host;
        private volatile Connection _connection;

        private static readonly Logger _logger = new Logger(typeof (ControlConnection));
        private readonly IReconnectionPolicy _reconnectionPolicy = new ExponentialReconnectionPolicy(2*1000, 5*60*1000);
        private readonly IReconnectionSchedule _reconnectionSchedule;
        private readonly Timer _reconnectionTimer;
        private readonly Session _session;
        private readonly BoolSwitch _shutdownSwitch = new BoolSwitch();
        private bool _isDisconnected;
        private readonly object _setupLock = new Object();
        private int _protocolVersion;

        /// <summary>
        /// Gets the recommended binary protocol version to be used for this cluster.
        /// </summary>
        internal int ProtocolVersion
        {
            get
            {
                if (_protocolVersion != 0)
                {
                    return _protocolVersion;
                }
                if (_isDisconnected)
                {
                    return _controlConnectionProtocolVersion;
                }
                return 1;
            }
        }

        /// <summary>
        /// The address of the endpoint used by the ControlConnection
        /// </summary>
        internal IPAddress BindAddress
        {
            get
            {
                if (_connection == null)
                {
                    return null;
                }
                return _connection.Address;
            }
        }

        internal ControlConnection(Cluster cluster,
                                   Policies policies,
                                   ProtocolOptions protocolOptions,
                                   PoolingOptions poolingOptions,
                                   SocketOptions socketOptions,
                                   ClientOptions clientOptions,
                                   IAuthProvider authProvider,
                                   IAuthInfoProvider authInfoProvider)
        {
            _cluster = cluster;
            _reconnectionSchedule = _reconnectionPolicy.NewSchedule();
            _reconnectionTimer = new Timer(ReconnectionClb, null, Timeout.Infinite, Timeout.Infinite);

            var config = new Configuration
            (
                policies,
                protocolOptions,
                poolingOptions,
                socketOptions,
                clientOptions,
                authProvider,
                authInfoProvider,
                new QueryOptions()
            );

            _session = new Session(cluster, config, "", _controlConnectionProtocolVersion);
        }

        public void Dispose()
        {
            Shutdown();
        }

        internal void Init()
        {
            _session.Init(false);
            SetupControlConnection();
        }

        public void Shutdown(int timeoutMs = Timeout.Infinite)
        {
            if (_shutdownSwitch.TryTake())
            {
                _reconnectionTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _reconnectionTimer.Dispose();
                _session.WaitForAllPendingActions(timeoutMs);
                _session.Dispose();
            }
        }

        /// <summary>
        /// Gets the next connection and setup the event listener for the host and connection.
        /// Not thread-safe.
        /// </summary>
        private void SetupEventListener()
        {
            _session.BinaryProtocolVersion = _controlConnectionProtocolVersion;
            var handler = new RequestHandler<RowSet>(_session, null, null);
            Connection connection = null;
            try
            {
                connection = handler.GetNextConnection(null);
            }
            catch (UnsupportedProtocolVersionException)
            {
                _logger.Verbose(String.Format("Unsupported protocol version {0}, trying with a lower version", _controlConnectionProtocolVersion));
                _controlConnectionProtocolVersion--;
                if (_controlConnectionProtocolVersion < 1)
                {
                    throw new DriverInternalError("Invalid protocol version");
                }
                SetupEventListener();
                return;
            }
            //Only 1 thread at a time here if the caller is locking
            
            //Unsubscribe to previous events
            if (_connection != null)
            {
                _connection.CassandraEventResponse -= OnConnectionCassandraEvent;
            }
            if (_host != null)
            {
                _host.Down -= OnHostDown;
            }

            connection.CassandraEventResponse += OnConnectionCassandraEvent;
            _connection = connection;
            var host = handler.Host;
            host.Down += OnHostDown;
            _host = host;
            //Register to events on the connection
            var registerTask = _connection.Send(new RegisterForEventRequest(_controlConnectionProtocolVersion, CassandraEventTypes));
            TaskHelper.WaitToComplete(registerTask, 10000);
            if (!(registerTask.Result is ReadyResponse))
            {
                throw new DriverInternalError("Expected ReadyResponse, obtained " + registerTask.Result.GetType().Name);
            }
        }

        private void OnHostDown(Host h, DateTimeOffset nextUpTime)
        {
            h.Down -= OnHostDown;
            _logger.Warning("Host " + h.Address + " used by the ControlConnection DOWN");

            Task.Factory.StartNew(() => SetupControlConnection());
        }

        private void OnConnectionCassandraEvent(object sender, CassandraEventArgs e)
        {
            Task.Factory.StartNew(() =>
            {
                if (e is TopologyChangeEventArgs)
                {
                    var tce = e as TopologyChangeEventArgs;
                    if (tce.What == TopologyChangeEventArgs.Reason.NewNode)
                    {
                        SetupControlConnection(true);
                        _cluster.Metadata.AddHost(tce.Address);
                        return;
                    }
                    if (tce.What == TopologyChangeEventArgs.Reason.RemovedNode)
                    {
                        _cluster.Metadata.RemoveHost(tce.Address);
                        SetupControlConnection(_connection != null && !tce.Address.Equals(_connection.Address));
                        return;
                    }
                }
                else if (e is StatusChangeEventArgs)
                {
                    var sce = e as StatusChangeEventArgs;
                    if (sce.What == StatusChangeEventArgs.Reason.Up)
                    {
                        _cluster.Metadata.BringUpHost(sce.Address, this);
                        return;
                    }
                    if (sce.What == StatusChangeEventArgs.Reason.Down)
                    {
                        _cluster.Metadata.SetDownHost(sce.Address, this);
                        return;
                    }
                }
                else if (e is SchemaChangeEventArgs)
                {
                    var ssc = e as SchemaChangeEventArgs;
                    if (!String.IsNullOrEmpty(ssc.Table))
                    {
                        RefreshTable(ssc.Keyspace, ssc.Table);
                        return;
                    }
                    if (ssc.What == SchemaChangeEventArgs.Reason.Dropped)
                    {
                        _cluster.Metadata.RemoveKeyspace(ssc.Keyspace);
                        return;
                    }
                    _cluster.Metadata.RefreshSingleKeyspace(ssc.What == SchemaChangeEventArgs.Reason.Created, ssc.Keyspace);
                }
            });
        }

        private void RefreshTable(string keyspaceName, string tableName)
        {
            _cluster.Metadata.RefreshTable(keyspaceName, tableName);
        }

        private void ReconnectionClb(object state)
        {
            try
            {
                SetupControlConnection();
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
            }
        }

        private void SetupControlConnection(bool refreshOnly = false)
        {
            lock (_setupLock)
            {
                try
                {
                    _reconnectionTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _logger.Info("Refreshing ControlConnection...");
                    if (!refreshOnly)
                    {
                        SetupEventListener();
                    }
                    RefreshNodeListAndTokenMap();
                    _isDisconnected = false;
                    _logger.Info("ControlConnection is listening on " + _connection.Address.ToString() + ", using binary protocol version " + _controlConnectionProtocolVersion);
                }
                catch (NoHostAvailableException)
                {
                    _isDisconnected = true;
                    if (!_shutdownSwitch.IsTaken())
                    {
                        _logger.Error("ControlConnection is lost. Retrying.");
                        _reconnectionTimer.Change(_reconnectionSchedule.NextDelayMs(), Timeout.Infinite);
                    }
                }
                catch (SocketException)
                {
                    _isDisconnected = true;
                    if (!_shutdownSwitch.IsTaken())
                    {
                        _logger.Error("ControlConnection is lost. Retrying.");
                        _reconnectionTimer.Change(_reconnectionSchedule.NextDelayMs(), Timeout.Infinite);
                    }
                }
                catch (Exception ex)
                {
                    _isDisconnected = true;
                    _logger.Error("Unexpected error occurred during ControlConnection refresh.", ex);
                }
            }
        }

        private void RefreshNodeListAndTokenMap()
        {
            _logger.Info("Refreshing NodeList and TokenMap.");
            //TODO: Handle connection failure
            var localRow = Query(SelectLocal).FirstOrDefault();
            var rsPeers = Query(SelectPeers);
            if (localRow == null)
            {
                _logger.Error("Local host metadata could not be retrieved");
                return;
            }
            var tokenMap = new Dictionary<IPAddress, HashSet<string>>();
            var partitioner = localRow.GetValue<string>("partitioner");
            UpdateLocalInfo(localRow, tokenMap);
            UpdatePeersInfo(rsPeers, tokenMap);
            _cluster.Metadata.RefreshKeyspaces();
            _cluster.Metadata.RebuildTokenMap(partitioner, tokenMap);
            _logger.Info("NodeList and TokenMap have been successfully refreshed!");
        }

        private void UpdateLocalInfo(Row row, IDictionary<IPAddress, HashSet<string>> tokenMap)
        {
            var localhost = _host;
            // Update cluster name, DC and rack for the one node we are connected to
            var clusterName = row.GetValue<string>("cluster_name");
            if (clusterName != null)
            {
                _cluster.Metadata.ClusterName = clusterName;
            }
            int protocolVersion;
            if (row.GetColumn("native_protocol_version") != null &&
                Int32.TryParse(row.GetValue<string>("native_protocol_version"), out protocolVersion))
            {
                //In Cassandra < 2
                //  there is no native protocol version column, it will get the default value
                _protocolVersion = protocolVersion;
            }
            localhost.SetLocationInfo(row.GetValue<string>("data_center"), row.GetValue<string>("rack"));
            var tokens = row.GetValue<IEnumerable<string>>("tokens") ?? new string[0];
            tokenMap.Add(localhost.Address, new HashSet<string>(tokens));
        }

        private void UpdatePeersInfo(IEnumerable<Row> rs, IDictionary<IPAddress, HashSet<string>> tokenMap)
        {
            var foundPeers = new HashSet<IPAddress>();
            var dcs = new List<string>();
            var racks = new List<string>();
            foreach (var row in rs)
            {
                var address = GetAddressForPeerHost(row);
                if (address == null)
                {
                    _logger.Error("No address found for host, ignoring it.");
                    continue;
                }
                foundPeers.Add(address);
                var host = _cluster.Metadata.GetHost(address);
                if (host == null)
                {
                    host = _cluster.Metadata.AddHost(address);
                }
                host.SetLocationInfo(row.GetValue<string>("data_center"), row.GetValue<string>("rack"));
                var tokens = row.GetValue<IEnumerable<string>>("tokens") ?? new string[0];
                tokenMap.Add(host.Address, new HashSet<string>(tokens));
            }

            // Removes all those that seems to have been removed (since we lost the control connection or not valid contact point)
            foreach (var address in _cluster.Metadata.AllReplicas())
            {
                if (!address.Equals(_host.Address) && !foundPeers.Contains(address))
                {
                    _cluster.Metadata.RemoveHost(address);
                }
            }
        }

        /// <summary>
        /// Uses system.peers values to build the Address translator
        /// </summary>
        internal static IPAddress GetAddressForPeerHost(Row row)
        {
            IPAddress address = null;
            if (!row.IsNull("rpc_address"))
            {
                address = row.GetValue<IPAddress>("rpc_address");
            }
            if (address == null && !row.IsNull("peer"))
            {
                address = row.GetValue<IPAddress>("peer");
                _logger.Error("No rpc_address found for host in peers system table.");
            }
            else if (BindAllAddress.Equals(address) && !row.IsNull("peer"))
            {
                address = row.GetValue<IPAddress>("peer");
            }
            return address;
        }

        /// <summary>
        /// Uses the active connection to execute a query
        /// </summary>
        public RowSet Query(string cqlQuery)
        {
            var request = new QueryRequest(_controlConnectionProtocolVersion, cqlQuery, false, QueryProtocolOptions.Default);
            var task = _connection.Send(request);
            TaskHelper.WaitToComplete(task, 10000);
            if (!(task.Result is ResultResponse) && !(((ResultResponse)task.Result).Output is OutputRows))
            {
                throw new DriverInternalError("Expected rows " + task.Result);
            }
            return ((task.Result as ResultResponse).Output as OutputRows).RowSet;
        }
    }
}
