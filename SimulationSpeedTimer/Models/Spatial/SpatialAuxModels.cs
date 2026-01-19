using System.ComponentModel.DataAnnotations.Schema;

namespace SimulationSpeedTimer.Models.Spatial
{
    public class SpatialObject
    {
        [Column("spatial_object_name")]
        public string SpatialObjectName { get; set; }

        [Column("entity_type")]
        public int EntityType { get; set; }

        [Column("IFF")]
        public int IFF { get; set; }

        [Column("RCS")]
        public double RCS { get; set; }
    }

    public class SpatialParent
    {
        [Column("spatial_object_name")]
        public string SpatialObjectName { get; set; }

        [Column("s_time")]
        public double STime { get; set; }

        [Column("p_spatial_object_name")]
        public string ParentSpatialObjectName { get; set; }
    }

    public class SpatialReferencePosition
    {
        [Column("Lat_Pos")]
        public double LatPos { get; set; }

        [Column("Lon_Pos")]
        public double LonPos { get; set; }
    }

    public class SpatialTimeInfo
    {
        [Column("current_time")]
        public double CurrentTime { get; set; }

        [Column("random_seed")]
        public string RandomSeed { get; set; }

        [Column("simulation_finish")]
        public bool SimulationFinish { get; set; }
    }
}
