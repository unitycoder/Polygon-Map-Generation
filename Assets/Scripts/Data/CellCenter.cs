using System.Collections.Generic;

namespace ProceduralMap
{
    public class CellCenter : MapPoint
    {
        public Biomes biome;

        public List<CellCenter> neighborCells = new List<CellCenter>();
        public List<CellEdge> borderEdges = new List<CellEdge>();
        public List<CellCorner> cellCorners = new List<CellCorner>();
    }
}