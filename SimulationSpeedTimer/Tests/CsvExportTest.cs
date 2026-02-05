using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using SimulationSpeedTimer.Services;

namespace SimulationSpeedTimer.Tests
{
    [TestFixture]
    public class CsvExportTest
    {
        private string _testFilePath;

        [SetUp]
        public void Setup()
        {
            _testFilePath = Path.Combine(Path.GetTempPath(), $"csv_test_{Guid.NewGuid()}.csv");
        }

        [TearDown]
        public void Teardown()
        {
            if (File.Exists(_testFilePath))
            {
                File.Delete(_testFilePath);
            }
        }

        [Test]
        public async Task Test_Export_ExpandoObject_Success()
        {
            // Arrange
            var data = new List<ExpandoObject>();
            dynamic row1 = new ExpandoObject();
            row1.Time = 0.1;
            row1.Value = 100;
            data.Add(row1);

            dynamic row2 = new ExpandoObject();
            row2.Time = 0.2;
            row2.Value = 200;
            data.Add(row2);

            var service = new CsvExportService();
            var headers = new List<string> { "Time", "Value" };

            // Act
            await service.ExportAsync(data, _testFilePath, headers);

            // Assert
            Assert.IsTrue(File.Exists(_testFilePath));
            var lines = File.ReadAllLines(_testFilePath);

            // Header + 2 Rows = 3 Lines
            Assert.AreEqual(3, lines.Length);
            Assert.AreEqual("Time,Value", lines[0]);
            Assert.AreEqual("0.1,100", lines[1]);
            Assert.AreEqual("0.2,200", lines[2]);
        }

        [Test]
        public async Task Test_Export_With_Comma_In_Value()
        {
            // Arrange
            var data = new List<ExpandoObject>();
            dynamic row1 = new ExpandoObject();
            row1.Name = "Hello, World";
            data.Add(row1);

            var service = new CsvExportService();
            var headers = new List<string> { "Name" };

            // Act
            await service.ExportAsync(data, _testFilePath, headers);

            // Assert
            var lines = File.ReadAllLines(_testFilePath);
            Assert.AreEqual("\"Hello, World\"", lines[1]);
        }
    }
}
