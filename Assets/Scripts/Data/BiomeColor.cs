using UnityEngine;
using UnityEngine.Tilemaps;

namespace ProceduralMap
{
    [System.Serializable]
    public class BiomeColor
    {
        public Biomes biome;
        public Color color;
        public Tile tile;
    }
}