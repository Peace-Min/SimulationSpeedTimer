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
        public SpatialDataCache SpatialDataCache { get; set; } = new SpatialDataCache();
    }

    public class SpatialDataCache
    {
        public Dictionary<string, List<SpatialData>> ByObjectName { get; set; } =
            new Dictionary<string, List<SpatialData>>();

        public Dictionary<double, Dictionary<string, List<SpatialData>>> ByTimeAndObjectName { get; set; } =
            new Dictionary<double, Dictionary<string, List<SpatialData>>>();
    }
}
