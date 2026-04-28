using System.Collections.Generic;

namespace SimulationSpeedTimer
{
    public class SchemaValidationReportSnapshot
    {
        public System.DateTime CreatedAt { get; set; }

        public string DbPath { get; set; }

        public bool IsSuccess { get; set; }

        public List<SchemaValidationConfiguredObjectEntry> ConfiguredObjects { get; set; }
            = new List<SchemaValidationConfiguredObjectEntry>();

        public List<SchemaValidationDbObjectEntry> DbObjects { get; set; }
            = new List<SchemaValidationDbObjectEntry>();
    }

    public class SchemaValidationConfiguredObjectEntry
    {
        public string ObjectName { get; set; }

        public string TableName { get; set; }

        public List<string> RequiredAttributes { get; set; } = new List<string>();

        public List<string> DbAttributes { get; set; } = new List<string>();

        public List<string> MissingAttributes { get; set; } = new List<string>();

        public bool IsSuccess { get; set; }
    }

    public class SchemaValidationDbObjectEntry
    {
        public string ObjectName { get; set; }

        public string TableName { get; set; }

        public List<string> Attributes { get; set; } = new List<string>();
    }
}
