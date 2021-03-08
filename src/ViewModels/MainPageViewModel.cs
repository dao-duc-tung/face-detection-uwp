﻿using FaceDetection.DistanceEstimator;
using FaceDetection.FaceDetector;
using FaceDetection.Models;
using FaceDetection.Utils;
using Microsoft.Toolkit.Uwp.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture.Frames;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

namespace FaceDetection.ViewModels
{
    public class MainPageViewModel : BaseNotifyPropertyChanged
    {
        private FrameModel _frameModel { get; } = FrameModel.Instance;
        private CameraControl _cameraControl { get; } = new CameraControl();
        private FaceDetectionControl _faceDetectionControl { get; } = new FaceDetectionControl();
        private FocalLengthDistanceEstimator _distanceEstimator;
        private SolidColorBrush _canvasObjectColor = new SolidColorBrush(Colors.LimeGreen);

        private Canvas _facesCanvas;
        private SoftwareBitmap _frameArrivedBackBuffer;
        private bool _isImageSourceUpdateRunning = false;
        private Image _imageControl;
        public Image ImageControl
        {
            get => _imageControl;
            set => SetProperty(ref _imageControl, value);
        }

        public ICommand ImageControlLoaded { get; set; }
        public ICommand FacesCanvasLoaded { get; set; }
        public ICommand LoadPhotoCmd { get; set; }
        public ICommand ToggleCameraCmd { get; set; }
        public ICommand ToggleFaceDetectionCmd { get; set; }

        #region Init
        public MainPageViewModel()
        {
            BindCommands();
            SubscribeEvents();
            Task.Run(LoadModelAsync).Wait();
            InitDistanceEstimator();
        }

        private void BindCommands()
        {
            ImageControlLoaded = new DelegateCommand<Image>(imageControl => _imageControl = imageControl);
            FacesCanvasLoaded = new DelegateCommand<Canvas>(facesCanvas => _facesCanvas = facesCanvas);
            LoadPhotoCmd = new DelegateCommand(async () => await LoadPhoto());
            ToggleCameraCmd = new DelegateCommand(async () => await ToggleCamera());
            ToggleFaceDetectionCmd = new DelegateCommand(async () => await ToggleFaceDetection());
        }

        private void SubscribeEvents()
        {
            _frameModel.PropertyChanged += _frameModel_PropertyChanged;
        }

        private async Task LoadModelAsync()
        {
            var config = (UltraFaceDetectorConfig)AppConfig.Instance.GetConfig(ConfigName.UltraFaceDetector);
            var modelLocalPath = config.ModelLocalPath;
            var uri = FileUtils.GetUriByLocalFilePath(modelLocalPath);
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            await _faceDetectionControl.LoadModelAsync(file);
        }

        private void InitDistanceEstimator()
        {
            var config = (FocalLengthDistanceEstimatorConfig)AppConfig.Instance.GetConfig(ConfigName.FocalLengthDistanceEstimator);
            _distanceEstimator = new FocalLengthDistanceEstimator(config);
        }
        #endregion Init

        #region Button Controls
        private async Task LoadPhoto()
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;
            using (var fileStream = await file.OpenAsync(FileAccessMode.Read))
            {
                if (_cameraControl.IsPreviewing) await TurnOffCameraPreview();
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(fileStream);
                SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                _frameModel.SoftwareBitmap = softwareBitmap;
            }
        }

        private async Task ToggleCamera()
        {
            if (!_cameraControl.IsPreviewing) await TurnOnCameraPreview();
            else await TurnOffCameraPreview();
            await ClearFacesCanvas();
        }

        private async Task ToggleFaceDetection()
        {
            _faceDetectionControl.IsFaceDetectionEnabled = !_faceDetectionControl.IsFaceDetectionEnabled;
            if (_faceDetectionControl.IsFaceDetectionEnabled)
            {
                _faceDetectionControl.FaceDetected += _faceDetector_FaceDetected;
                await RunFaceDetection();
            }
            else
            {
                _faceDetectionControl.FaceDetected -= _faceDetector_FaceDetected;
            }
            await ClearFacesCanvas();
        }
        #endregion Button Controls

        #region Camera Control
        private async Task TurnOnCameraPreview()
        {
            if (ImageControl.Source == null)
            {
                await DispatcherHelper.ExecuteOnUIThreadAsync(() => {
                    ImageControl.Source = new SoftwareBitmapSource();
                });
            }
            await InitCameraAsync();
            await StartPreviewAsync();
        }

        private async Task TurnOffCameraPreview()
        {
            await StopPreviewAsync();
            await DispatcherHelper.ExecuteOnUIThreadAsync(() => { ImageControl.Source = null; });
        }

        private async Task InitCameraAsync() => await _cameraControl.InitCameraAsync();

        private async Task StartPreviewAsync()
        {
            await _cameraControl.StartPreviewAsync();
            _cameraControl.FrameArrived += OnFrameArrived;
        }

        private async Task StopPreviewAsync()
        {
            _cameraControl.FrameArrived -= OnFrameArrived;
            await _cameraControl.StopPreviewAsync();
        }

        private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            using (var frame = sender.TryAcquireLatestFrame())
            {
                var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (bmp != null)
                {
                    if (bmp.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                        || bmp.BitmapAlphaMode != BitmapAlphaMode.Ignore
                        || bmp.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                    {
                        bmp = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                    }
                    // Swap the processed frame to _backBuffer and dispose of the unused image.
                    bmp = Interlocked.Exchange(ref _frameArrivedBackBuffer, bmp);
                    bmp?.Dispose();

                    var task = DispatcherHelper.ExecuteOnUIThreadAsync(async () => {
                        // Don't let two copies of this task run at the same time.
                        if (_isImageSourceUpdateRunning) return;
                        _isImageSourceUpdateRunning = true;

                        // Keep draining frames from the backbuffer until the backbuffer is empty.
                        SoftwareBitmap checkingBmp = null, latestBmp = null;
                        while (true)
                        {
                            checkingBmp = Interlocked.Exchange(ref _frameArrivedBackBuffer, null);
                            if (checkingBmp == null) break;
                            latestBmp = checkingBmp;
                            var imageSource = (SoftwareBitmapSource)ImageControl.Source;
                            await imageSource.SetBitmapAsync(latestBmp);
                        }
                        if (latestBmp != null) _frameModel.SoftwareBitmap = latestBmp;

                        _isImageSourceUpdateRunning = false;
                    });
                }
            }
        }
        #endregion Camera Control

        #region Face Detection Control
        private void _frameModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_frameModel.SoftwareBitmap))
            {
                Task.Run(async () => await RunFaceDetection());
            }
        }

        private async void _faceDetector_FaceDetected(object sender, IReadOnlyList<FaceBoundingBox> faceBoundingBoxes, System.Drawing.Size originalSize)
        {
            await DispatcherHelper.ExecuteOnUIThreadAsync(() => HighlightDetectedFaces(faceBoundingBoxes, originalSize));
        }

        private async Task RunFaceDetection()
        {
            var bmp = _frameModel.SoftwareBitmap;
            if (bmp == null) return;
            await _faceDetectionControl.RunFaceDetection(bmp);
        }

        private void HighlightDetectedFaces(IReadOnlyList<FaceBoundingBox> faces, System.Drawing.Size originalSize)
        {
            _facesCanvas.Children.Clear();
            var IsBoxSizeDisplayed = ((MainConfig)AppConfig.Instance.GetConfig(ConfigName.Main)).IsBoxSizeDisplayed;
            for (int i = 0; i < faces.Count; ++i)
            {
                var bb = FaceDetectionControl.ScaleBoundingBox(faces[i], originalSize);
                Rectangle faceBB = ConvertPreviewToUiRectangle(bb, originalSize);
                faceBB.StrokeThickness = 2;
                faceBB.Stroke = _canvasObjectColor;
                _facesCanvas.Children.Add(faceBB);

                var distStr = _distanceEstimator.ComputeDistance(faces[i]).ToString("n0") + " cm";
                TextBlock distance = UIUtils.CreateTextBlock(distStr, _canvasObjectColor, Canvas.GetLeft(faceBB) + 5, Canvas.GetTop(faceBB));
                _facesCanvas.Children.Add(distance);

                if (_faceDetectionControl.IsFaceDetectionEnabled)
                {
                    var fpsStr = $"Face Detection FPS: {_faceDetectionControl.FPS.ToString("n2")}";
                    TextBlock faceDetectionFPS = UIUtils.CreateTextBlock(fpsStr, _canvasObjectColor, 20, 20);
                    _facesCanvas.Children.Add(faceDetectionFPS);
                }

                if (IsBoxSizeDisplayed)
                {
                    var origSizeStr = $"Orig Size: {faces[i].Width.ToString("n2")} x {faces[i].Height.ToString("n2")}";
                    TextBlock origSize = UIUtils.CreateTextBlock(origSizeStr, _canvasObjectColor, Canvas.GetLeft(faceBB), Canvas.GetTop(faceBB) - 40);
                    _facesCanvas.Children.Add(origSize);

                    var scaledSizeStr= $"Scaled Size: {faceBB.Width.ToString("n2")} x {faceBB.Height.ToString("n2")}";
                    TextBlock scaledSize = UIUtils.CreateTextBlock(scaledSizeStr, _canvasObjectColor, Canvas.GetLeft(faceBB), Canvas.GetTop(faceBB) - 20);
                    _facesCanvas.Children.Add(scaledSize);
                }
            }
        }

        private Rectangle ConvertPreviewToUiRectangle(FaceBoundingBox faceBox, System.Drawing.Size streamSize)
        {
            var result = new Rectangle();
            double streamWidth = streamSize.Width;
            double streamHeight = streamSize.Height;

            if (streamWidth == 0 || streamHeight == 0) return result;

            // Get the rectangle that is occupied by the actual video feed
            var currWindowFrame = Window.Current.Bounds;
            var windowSize = new Size(currWindowFrame.Width, currWindowFrame.Height);
            var previewInUI = GetDisplayRectInControl(streamSize, windowSize);
            var scaleWidth = previewInUI.Width / streamWidth;
            var scaleHeight = previewInUI.Height / streamHeight;

            //Scale the width and height from preview stream coordinates to window coordinates
            result.Width = faceBox.Width * scaleWidth;
            result.Height = faceBox.Height * scaleHeight;

            // Scale the X and Y coordinates from preview stream coordinates to window coordinates
            var x = previewInUI.X + faceBox.X0 * scaleWidth;
            var y = previewInUI.Y + faceBox.Y0 * scaleHeight;
            Canvas.SetLeft(result, x);
            Canvas.SetTop(result, y);

            return result;
        }

        private Rect GetDisplayRectInControl(System.Drawing.Size streamSize, Size windowSize)
        {
            var result = new Rect();
            if (windowSize.Height < 1 || windowSize.Width < 1 ||
                streamSize.Height == 0 || streamSize.Width == 0) return result;

            var streamWidth = streamSize.Width;
            var streamHeight = streamSize.Height;

            // Start by assuming the preview display area in the control spans entire width and height
            var actualWidth = windowSize.Width;
            var actualHeight = windowSize.Height;
            result.Width = actualWidth;
            result.Height = actualHeight;

            // If UI is "wider" than preview, letterboxing will be on the sides
            if ((actualWidth / actualHeight > streamWidth / (double)streamHeight))
            {
                var scale = actualHeight / streamHeight;
                var scaledWidth = streamWidth * scale;
                result.X = (actualWidth - scaledWidth) / 2.0;
                result.Width = scaledWidth;
            }
            else // Preview stream is "wider" than UI, so letterboxing will be on the top+bottom
            {
                var scale = actualWidth / streamWidth;
                var scaledHeight = streamHeight * scale;
                result.Y = (actualHeight - scaledHeight) / 2.0;
                result.Height = scaledHeight;
            }
            return result;
        }

        private async Task ClearFacesCanvas() => await DispatcherHelper.ExecuteOnUIThreadAsync(() => _facesCanvas.Children.Clear());
        #endregion Face Detection Control
    }
}
