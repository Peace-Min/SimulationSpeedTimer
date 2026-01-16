using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace SimulationSpeedTimer.Tests
{
    [TestFixture]
    public class PostAnalysisServiceTest
    {
        private string _testDbPath;

        [SetUp]
        public void Setup()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"PostAnalysisTest_{Guid.NewGuid()}.db");
            
            // 테스트용 DB 생성 및 데이터 주입
            using (var conn = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
            {
                conn.Open();

                // 1. 메타데이터 테이블 생성
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE Object_Info (object_name TEXT, table_name TEXT);
                        CREATE TABLE Column_Info (table_name TEXT, column_name TEXT, attribute_name TEXT, data_type TEXT);
                        
                        INSERT INTO Object_Info VALUES ('SAM001', 'Object_Table_0');
                        INSERT INTO Column_Info VALUES ('Object_Table_0', 'COL1', 'Velocity', 'DOUBLE');
                        INSERT INTO Column_Info VALUES ('Object_Table_0', 'COL2', 'Altitude', 'DOUBLE');
                    ";
                    cmd.ExecuteNonQuery();
                }

                // 2. 물리 테이블 생성 및 데이터 주입
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE Object_Table_0 (s_time DOUBLE, COL1 DOUBLE, COL2 DOUBLE);
                        
                        -- 1.0초 데이터
                        INSERT INTO Object_Table_0 VALUES (1.0, 100.5, 5000.0);
                        -- 2.0초 데이터
                        INSERT INTO Object_Table_0 VALUES (2.0, 120.5, 5100.0);
                        -- 3.0초 데이터 (일부 NULL 테스트)
                        INSERT INTO Object_Table_0 VALUES (3.0, 130.5, NULL);
                    ";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (File.Exists(_testDbPath))
            {
                try { File.Delete(_testDbPath); } catch { }
            }
        }

        [Test]
        public void LoadAllData_ShouldLoadAndMapCorrectly()
        {
            // Act
            var result = PostAnalysisService.LoadAllData(_testDbPath);

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Count, "테이블 1개가 로드되어야 함");

            var table = result.First();
            Assert.AreEqual("SAM001", table.TableName, "논리적 이름(SAM001)으로 매핑되어야 함");

            // 시간별 데이터 검증
            Assert.AreEqual(3, table.TimeData.Count, "총 3개의 시간 데이터(1.0, 2.0, 3.0)가 있어야 함");
            
            // 1.0초 데이터 검증
            Assert.IsTrue(table.TimeData.ContainsKey(1.0));
            var row1 = table.TimeData[1.0];
            Assert.AreEqual(100.5, Convert.ToDouble(row1["Velocity"]), 0.0001); // COL1 -> Velocity
            Assert.AreEqual(5000.0, Convert.ToDouble(row1["Altitude"]), 0.0001); // COL2 -> Altitude

            // 3.0초 데이터 검증 (NULL 처리)
            Assert.IsTrue(table.TimeData.ContainsKey(3.0));
            var row3 = table.TimeData[3.0];
            Assert.IsTrue(row3.ContainsKey("Velocity"));
            Assert.IsFalse(row3.ContainsKey("Altitude"), "NULL인 값은 Dictionary에 포함되지 않아야 함");
        }

        [Test]
        public void LoadAllData_ShouldHandleMissingMetadata()
        {
            // Object_Info에 없는 테이블(PhysicalOnly)도 로드되는지 확인
            using (var conn = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE Extra_Table (s_time DOUBLE, RawData TEXT);";
                    cmd.CommandText += "INSERT INTO Extra_Table VALUES (5.0, 'RawValue');";
                    cmd.ExecuteNonQuery();
                }
            }

            // Act: 다시 로드 (메타데이터 갱신은 안 됨, Extra_Table은 Object_Info에 없음)
            // 주의: 현재 구현상 LoadAnalysisSchema는 Object_Info/Column_Info 만 읽음.
            // 물리 테이블만 있고 메타데이터가 아예 없으면 로드 대상에서 제외될 수 있음.
            // PostAnalysisService 코드를 보면 "schema.Tables"를 순회하므로, 스키마에 없으면 로드 안 됨.
            // 하지만 Column_Info라도 있으면 로드 시도함.
            
            // 이 테스트에서는 "Object_Info"에는 없지만 "Column_Info"에 추가해서 로드되는지 확인
             using (var conn = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO Column_Info VALUES ('Extra_Table', 'RawData', 'RawDataAttribute', 'STRING');";
                    cmd.ExecuteNonQuery();
                }
            }

            var result = PostAnalysisService.LoadAllData(_testDbPath);
            
            // Assert
            var extraTable = result.FirstOrDefault(t => t.TableName == "Extra_Table");
            Assert.IsNotNull(extraTable, "Column_Info에만 있어도 로드되어야 함 (물리명=논리명 fallback)");
            Assert.AreEqual("RawValue", extraTable.TimeData[5.0]["RawDataAttribute"]);
        }
    }
}
