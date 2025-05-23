using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using EnumsNET;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.World;
using QuantumCore.Caching;
using QuantumCore.Core.Event;
using QuantumCore.Core.Utils;
using QuantumCore.Game.Services;
using QuantumCore.Game.World.Entities;

// using QuantumCore.Core.API;

namespace QuantumCore.Game.World
{
    public class Map : IMap
    {
        public const uint MapUnit = 25600;
        private const int SPAWN_BASE_OFFSET = 5;
        public const int SPAWN_POSITION_MULTIPLIER = 100;
        private const int SPAWN_ROTATION_SLICE_DEGREES = 45;
        public string Name { get; private set; }
        public Coordinates Position { get; private set; }
        public uint UnitX => Position.X / MapUnit;
        public uint UnitY => Position.Y / MapUnit;
        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public TownCoordinates? TownCoordinates { get; private set; }

        public IWorld World => _world;
        public IReadOnlyCollection<IEntity> Entities => _entities;

        private readonly ObservableGauge<int> _entityGauge;
        private readonly List<IEntity> _entities = new();
        private readonly QuadTree _quadTree;
        private readonly List<SpawnPoint> _spawnPoints = new();

        private readonly List<IEntity> _nearby = new();
        private readonly List<IEntity> _remove = new();
        private readonly ConcurrentQueue<IEntity> _pendingRemovals = new();
        private readonly ConcurrentQueue<IEntity> _pendingSpawns = new();
        private readonly IMonsterManager _monsterManager;
        private readonly IAnimationManager _animationManager;
        private readonly ICacheManager _cacheManager;
        private readonly IWorld _world;
        private readonly ILogger _logger;
        private readonly ISpawnPointProvider _spawnPointProvider;
        private readonly IDropProvider _dropProvider;
        private readonly IItemManager _itemManager;
        private readonly IServerBase _server;
        private readonly IServiceProvider _serviceProvider;

        public Map(IMonsterManager monsterManager, IAnimationManager animationManager, ICacheManager cacheManager,
            IWorld world, ILogger logger, ISpawnPointProvider spawnPointProvider,
            IDropProvider dropProvider, IItemManager itemManager, IServerBase server, string name, Coordinates position,
            uint width, uint height, TownCoordinates? townCoordinates, IServiceProvider serviceProvider)
        {
            _monsterManager = monsterManager;
            _animationManager = animationManager;
            _cacheManager = cacheManager;
            _world = world;
            _logger = logger;
            _spawnPointProvider = spawnPointProvider;
            _dropProvider = dropProvider;
            _itemManager = itemManager;
            _server = server;
            _serviceProvider = serviceProvider;
            Name = name;
            Position = position;
            Width = width;
            Height = height;

            TownCoordinates = townCoordinates is not null
                ? new TownCoordinates
                {
                    Jinno = Position + townCoordinates.Jinno * SPAWN_POSITION_MULTIPLIER,
                    Chunjo = Position + townCoordinates.Chunjo * SPAWN_POSITION_MULTIPLIER,
                    Shinsoo = Position + townCoordinates.Shinsoo * SPAWN_POSITION_MULTIPLIER,
                    Common = Position + townCoordinates.Common * SPAWN_POSITION_MULTIPLIER
                }
                : null;_serviceProvider = serviceProvider;            _quadTree = new QuadTree((int)position.X, (int)position.Y, (int)(width * MapUnit), (int)(height * MapUnit), 20);
            _entityGauge = GameServer.Meter.CreateObservableGauge($"Map:{name}:EntityCount", () => Entities.Count);
        }

        public async Task Initialize()
        {
            _logger.LogDebug("Load map {Name} at {Position} (size {Width}x{Height})", Name, Position, Width, Height);

            await _cacheManager.Set($"maps:{Name}", $"{_server.IpAddress}:{_server.Port}");
            await _cacheManager.Publish("maps", $"{Name} {_server.IpAddress}:{_server.Port}");

            _spawnPoints.AddRange(await _spawnPointProvider.GetSpawnPointsForMap(Name));

            _logger.LogDebug("Loaded {SpawnPointsCount} spawn points for map {MapName}", _spawnPoints.Count, Name);

            // Populate map
            foreach (var spawnPoint in _spawnPoints)
            {
                var monsterGroup = new MonsterGroup {SpawnPoint = spawnPoint};
                SpawnGroup(monsterGroup);
            }
        }

        public void Update(double elapsedTime)
        {
            // HookManager.Instance.CallHook<IHookMapUpdate>(this, elapsedTime);

            while (_pendingSpawns.TryDequeue(out var entity))
            {
                if (!_quadTree.Insert(entity)) continue;

                // Add this entity to all entities nearby
                var nearby = new List<IEntity>();
                EEntityType? filter = null;
                if (entity.Type != EEntityType.Player)
                {
                    // if we aren't a player only players are relevant for nearby
                    filter = EEntityType.Player;
                }

                _quadTree.QueryAround(nearby, entity.PositionX, entity.PositionY, Entity.ViewDistance, filter);
                foreach (var e in nearby)
                {
                    if (e == entity) continue;
                    entity.AddNearbyEntity(e);
                    e.AddNearbyEntity(entity);
                }

                _entities.Add(entity);
                entity.Map = this;
            }

            while (_pendingRemovals.TryDequeue(out var entity))
            {
                _entities.Remove(entity);

                entity.OnDespawn();
                if (entity is IDisposable dis)
                {
                    dis.Dispose();
                }

                // Remove this entity from all nearby entities
                foreach (var e in entity.NearbyEntities)
                {
                    e.RemoveNearbyEntity(entity);
                }

                // Remove map from the entity
                entity.Map = null;

                // Remove entity from the quad tree
                _quadTree.Remove(entity);
            }

            foreach (var entity in _entities)
            {
                entity.Update(elapsedTime);

                if (entity.PositionChanged)
                {
                    entity.PositionChanged = false;

                    // Update position in our quad tree (used for faster nearby look up)
                    _quadTree.UpdatePosition(entity);

                    if (entity.Type == EEntityType.Player)
                    {
                        // Check which entities are relevant for nearby
                        EEntityType? filter = null;
                        if (entity.Type != EEntityType.Player)
                        {
                            // if we aren't a player only players are relevant for nearby
                            filter = EEntityType.Player;
                        }

                        // Update entities nearby
                        _quadTree.QueryAround(_nearby, entity.PositionX, entity.PositionY, Entity.ViewDistance,
                            filter);

                        // Check nearby entities and mark all entities which are too far away now
                        foreach (var e in entity.NearbyEntities)
                        {
                            // Remove this entity from our temporary list as they are already in it
                            if (!_nearby.Remove(e))
                            {
                                // If it wasn't in our temporary list it is no longer in view
                                _remove.Add(e);
                            }
                        }

                        // Remove previously marked entities on both sides
                        foreach (var e in _remove)
                        {
                            e.RemoveNearbyEntity(entity);
                            entity.RemoveNearbyEntity(e);
                        }

                        // Add new nearby entities on both sides
                        foreach (var e in _nearby)
                        {
                            if (e == entity)
                            {
                                continue; // do not add ourself!
                            }

                            e.AddNearbyEntity(entity);
                            entity.AddNearbyEntity(e);
                        }

                        // Clear our temporary lists
                        _nearby.Clear();
                        _remove.Clear();
                    }
                }
            }
        }

        private void SpawnGroup(MonsterGroup groupInstance)
        {
            var spawnPoint = groupInstance.SpawnPoint;

            if (spawnPoint is null) return;

            switch (spawnPoint.Type)
            {
                case ESpawnPointType.GroupCollection:
                    var groupCollection = _world.GetGroupCollection(spawnPoint.Monster);
                    if (groupCollection != null)
                    {
                        var index = Random.Shared.Next(0, groupCollection.Groups.Count);
                        var collectionGroup = groupCollection.Groups[index];
                        var group = _world.GetGroup(collectionGroup.Id);
                        if (group != null)
                        {
                            if (collectionGroup.Probability < 1)
                            {
                                var rand = Random.Shared.NextSingle();
                                if (rand > collectionGroup.Probability)
                                {
                                    SpawnGroup(groupInstance, spawnPoint, group);
                                }
                            }
                            else
                            {
                                SpawnGroup(groupInstance, spawnPoint, group);
                            }
                        }
                    }

                    break;
                case ESpawnPointType.Group:
                    {
                        var group = _world.GetGroup(spawnPoint.Monster);
                        if (group != null)
                        {
                            SpawnGroup(groupInstance, spawnPoint, group);
                        }

                        break;
                    }
                case ESpawnPointType.Monster:
                    {
                        var monster = SpawnMonster(spawnPoint.Monster, spawnPoint);

                        spawnPoint.CurrentGroup = groupInstance;
                        groupInstance.Monsters.Add(monster);
                        monster.Group = groupInstance;

                        break;
                    }
                default:
                    _logger.LogWarning("Unknown spawn point type: {SpawnPointType}", spawnPoint.Type);
                    break;
            }
        }

        private void SpawnGroup(MonsterGroup groupInstance, SpawnPoint spawnPoint, SpawnGroup group)
        {
            spawnPoint.CurrentGroup = groupInstance;

            var leader = SpawnMonster(group.Leader, spawnPoint);
            groupInstance.Monsters.Add(leader);
            leader.Group = groupInstance;

            foreach (var member in group.Members)
            {
                var monster = SpawnMonster(member.Id, spawnPoint);

                groupInstance.Monsters.Add(monster);
                monster.Group = groupInstance;
            }
        }

        private MonsterEntity SpawnMonster(uint id, SpawnPoint spawnPoint)
        {
            var baseX = spawnPoint.X;
            var baseY = spawnPoint.Y;
            if (spawnPoint.RangeX != 0)
            {
                baseX += RandomNumberGenerator.GetInt32(-spawnPoint.RangeX, spawnPoint.RangeX);
            }

            if (spawnPoint.RangeY != 0)
            {
                baseY += RandomNumberGenerator.GetInt32(-spawnPoint.RangeY, spawnPoint.RangeY);
            }

            var monster = new MonsterEntity(_monsterManager, _dropProvider, _animationManager, _serviceProvider, this,
                _logger,
                _itemManager,
                id,
                0,
                0
            );

            if (monster.Proto.AiFlag.HasAnyFlags(EAiFlags.NoMove))
            {
                monster.PositionX = (int)(Position.X + baseX * SPAWN_POSITION_MULTIPLIER);
                monster.PositionY = (int)(Position.Y + baseY * SPAWN_POSITION_MULTIPLIER);
                var compassDirection = (int)spawnPoint.Direction - 1;

                if (compassDirection < 0 || compassDirection > (int)Enum.GetValues<ESpawnPointDirection>().Last())
                {
                    compassDirection = (int)ESpawnPointDirection.Random;
                }

                var rotation = SPAWN_ROTATION_SLICE_DEGREES * compassDirection;
                monster.Rotation = rotation;
            }
            else
            {
                monster.PositionX = (int)Position.X +
                                    (baseX + RandomNumberGenerator.GetInt32(-SPAWN_BASE_OFFSET, SPAWN_BASE_OFFSET)) *
                                    SPAWN_POSITION_MULTIPLIER;
                monster.PositionY = (int)Position.Y +
                                    (baseY + RandomNumberGenerator.GetInt32(-SPAWN_BASE_OFFSET, SPAWN_BASE_OFFSET)) *
                                    SPAWN_POSITION_MULTIPLIER;
            }

            if (monster.Rotation == 0)
            {
                monster.Rotation = RandomNumberGenerator.GetInt32(0, 360);
            }

            _world.SpawnEntity(monster);
            return monster;
        }

        public void EnqueueGroupRespawn(MonsterGroup group)
        {
            if (group.SpawnPoint == null) return;

            EventSystem.EnqueueEvent(() =>
            {
                // TODO
                SpawnGroup(group);
                return 0;
            }, group.SpawnPoint.RespawnTime * 1000);
        }

        public bool IsPositionInside(int x, int y)
        {
            return x >= Position.X && x < Position.X + Width * MapUnit && y >= Position.Y &&
                   y < Position.Y + Height * MapUnit;
        }

        public void SpawnEntity(IEntity entity)
        {
            _pendingSpawns.Enqueue(entity);
        }

        /// <summary>
        /// Add a ground item which will automatically get destroyed after configured time
        /// </summary>
        /// <param name="item">Item to add on the ground, should not have any owner!</param>
        /// <param name="x">Position X</param>
        /// <param name="y">Position Y</param>
        /// <param name="amount">Only used for gold as we have a higher limit here</param>
        /// <param name="ownerName"></param>
        public void AddGroundItem(ItemInstance item, int x, int y, uint amount = 0, string? ownerName = null)
        {
            var groundItem = new GroundItem(_animationManager, _world.GenerateVid(), item, amount, ownerName)
            {
                PositionX = x, PositionY = y
            };

            SpawnEntity(groundItem);
        }

        /// <summary>
        /// Should only be called by World
        /// </summary>
        /// <param name="entity"></param>
        public void DespawnEntity(IEntity entity)
        {
            _logger.LogDebug("Despawn {Entity}", entity);

            // Remove entity from entities list in the next update
            _pendingRemovals.Enqueue(entity);
        }

        public IEntity? GetEntity(uint vid)
        {
            return _entities?.Find(e => e.Vid == vid);
        }
    }
}
