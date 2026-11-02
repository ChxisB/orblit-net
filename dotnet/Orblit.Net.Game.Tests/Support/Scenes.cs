using Orblit.Scene;
using Orblit.VectorMath;

namespace Orblit.Net.Game.Tests;

/// <summary>Scenes and entities the networked tests share.</summary>
internal static class Scenes
{
    public static SceneDocument World(params SceneEntity[] entities) => new(entities: entities);

    public static SceneEntity Tagged(
        string id,
        double x,
        double y,
        double z,
        NetAuthority authority = NetAuthority.Server,
        string[]? sync = null) => new(
        id,
        id,
        components: new Dictionary<string, SceneComponent>
        {
            [SceneComponents.Transform] = new TransformComponent(new Vector3(x, y, z)),
            [SceneComponents.Net] = new NetComponent(authority, sync),
        });

    public static SceneDiff Add(SceneEntity entity) => new([new AddEntity(entity.ToJson())]);

    public static SceneDiff Remove(SceneDocument scene, string id) => new([new RemoveEntity(scene[id]!.ToJson())]);
}
