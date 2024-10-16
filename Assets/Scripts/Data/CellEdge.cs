﻿namespace ProceduralMap
{
	public class CellEdge
	{
		public int index;

		//Delaunay triangulation edges
		public CellCenter d0 = new CellCenter();
		public CellCenter d1 = new CellCenter();

		//Voronoi edges
		public CellCorner v0 = new CellCorner();
		public CellCorner v1 = new CellCorner();

		public int waterVolume;
	}
}