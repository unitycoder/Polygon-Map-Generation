using UnityEngine;

namespace ProceduralMap
{
	[CreateAssetMenu(menuName = "Procedural Shape/Square", order = 1250)]
    public class IslandShape : ScriptableObject
	{
		public virtual bool IsPointInsideShape(Vector2 point, Vector2 mapSize, int seed = 0)
		{
			Random.InitState(seed);
			return true;
		}
	}
}