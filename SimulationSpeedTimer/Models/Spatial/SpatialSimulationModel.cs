using System.Collections.Generic;

namespace SimulationSpeedTimer.Models.Spatial
{
    public class SpatialSimulationModel
    {
        public List<SpatialData> SpatialData { get; set; } = new List<SpatialData>();
        public List<SpatialVisualData> SpatialVisualData { get; set; } = new List<SpatialVisualData>();
        public List<SpatialObject> SpatialObjects { get; set; } = new List<SpatialObject>();
        public List<SpatialParent> SpatialParents { get; set; } = new List<SpatialParent>();
        public List<SpatialReferencePosition> ReferencePositions { get; set; } = new List<SpatialReferencePosition>();
        public List<SpatialTimeInfo> TimeInfos { get; set; } = new List<SpatialTimeInfo>();
    }
}
