using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SimulationSpeedTimer.Models.Spatial
{
    public abstract class SpatialDataBase
    {
        [Column("spatial_object_name")]
        public string SpatialObjectName { get; set; }
        
        [Column("s_time")]
        public double STime { get; set; }
        
        [Column("Damage_State")]
        public int DamageState { get; set; }
        
        [Column("Player_State")]
        public int PlayerState { get; set; }
        
        [Column("Lat_Pos")]
        public double LatPos { get; set; }
        
        [Column("Lon_Pos")]
        public double LonPos { get; set; }
        
        [Column("Alt_Pos")]
        public double AltPos { get; set; }
        
        [Column("Yaw")]
        public double Yaw { get; set; }
        
        [Column("Pitch")]
        public double Pitch { get; set; }
        
        [Column("Roll")]
        public double Roll { get; set; }
        
        [Column("Lat_Vel")]
        public double LatVel { get; set; }
        
        [Column("Lon_Vel")]
        public double LonVel { get; set; }
        
        [Column("Alt_Vel")]
        public double AltVel { get; set; }
        
        [Column("Yaw_Vel")]
        public double YawVel { get; set; }
        
        [Column("Pitch_Vel")]
        public double PitchVel { get; set; }
        
        [Column("Roll_Vel")]
        public double RollVel { get; set; }
        
        [Column("Lat_Acc")]
        public double LatAcc { get; set; }
        
        [Column("Lon_Acc")]
        public double LonAcc { get; set; }
        
        [Column("Alt_Acc")]
        public double AltAcc { get; set; }
        
        [Column("Yaw_Acc")]
        public double YawAcc { get; set; }
        
        [Column("Pitch_Acc")]
        public double PitchAcc { get; set; }
        
        [Column("Roll_Acc")]
        public double RollAcc { get; set; }
        
        // Extended Attributes
        public double ExtAttribute_0 { get; set; }
        public double ExtAttribute_1 { get; set; }
        public double ExtAttribute_2 { get; set; }
        public double ExtAttribute_3 { get; set; }
        public double ExtAttribute_4 { get; set; }
        public double ExtAttribute_5 { get; set; }
        public double ExtAttribute_6 { get; set; }
        public double ExtAttribute_7 { get; set; }
        public double ExtAttribute_8 { get; set; }
        public double ExtAttribute_9 { get; set; }
        public double ExtAttribute_10 { get; set; }
        public double ExtAttribute_11 { get; set; }
        public double ExtAttribute_12 { get; set; }
        public double ExtAttribute_13 { get; set; }
        public double ExtAttribute_14 { get; set; }
        public double ExtAttribute_15 { get; set; }
        public double ExtAttribute_16 { get; set; }
        public double ExtAttribute_17 { get; set; }
        public double ExtAttribute_18 { get; set; }
        public double ExtAttribute_19 { get; set; }
    }

    public class SpatialData : SpatialDataBase
    {
        [Column("player_name")]
        public string PlayerName { get; set; }
    }

    public class SpatialVisualData : SpatialDataBase
    {
        // Identical to Base but keeps separate identity
    }
}
