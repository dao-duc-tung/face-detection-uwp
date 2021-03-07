﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaceDetection.Utils
{
    public static class ConfigName
    {
        public static readonly string UltraFaceDetector = "UltraFaceDetector";
        public static readonly string FocalLengthDistanceEstimator = "FocalLengthDistanceEstimator";
    }

    public static class ConfigLocalPath
    {
        public static readonly string UltraFaceDetector = "Assets/UltraFaceDetectorConfig.json";
        public static readonly string FocalLengthDistanceEstimator = "Assets/FocalLengthDistanceEstimatorConfig.json";
    }
}
