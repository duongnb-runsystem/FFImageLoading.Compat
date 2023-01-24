﻿using FFImageLoading.Maui;
using FFImageLoading.Work;
using FFImageLoading.Extensions;
using FFImageLoading.Maui.Args;
using System;
using System.ComponentModel;
using Windows.Graphics.Imaging;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using System.IO;
using System.Threading;
using System.Reflection;
using System.Linq;
using FFImageLoading.Helpers;
using FFImageLoading.Maui.Platform;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls;

using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;

namespace FFImageLoading.Forms.Platform
{
    /// <summary>
    /// CachedImage Implementation
    /// </summary>
    [Preserve(AllMembers = true)]
    public class CachedImageHandler : ViewHandler<CachedImage, Microsoft.UI.Xaml.Controls.Image>
    {
        [RenderWith(typeof(CachedImageHandler))]
        internal class _CachedImageRenderer
        {
        }

        private bool _measured;
        private IScheduledWork _currentTask;
        private ImageSourceBinding _lastImageSource;
        private bool _isDisposed = false;

		public CachedImageHandler() : base(ViewHandler.ViewMapper, ViewHandler.ViewCommandMapper)
		{
		}

		public CachedImageHandler(IPropertyMapper mapper, CommandMapper commandMapper = null) : base(mapper, commandMapper)
		{
		}

		/// <summary>
		///   Used for registration with dependency service
		/// </summary>
		public static void Init()
        {
            CachedImage.IsRendererInitialized = true;
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            ScaleHelper.InitAsync();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }

		public Microsoft.UI.Xaml.Controls.Image Control { get; private set; }

		protected override Microsoft.UI.Xaml.Controls.Image CreatePlatformView()
		{
			Control = new Microsoft.UI.Xaml.Controls.Image()
			{
				Stretch = GetStretch(Aspect.AspectFill),
			};
			return Control;
		}

		protected override void ConnectHandler(Microsoft.UI.Xaml.Controls.Image platformView)
		{
			VirtualView.PropertyChanged += VirtualView_PropertyChanged;
			Control.ImageOpened += OnImageOpened;


			Control.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center;
			Control.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;


			VirtualView.InternalReloadImage = new Action(ReloadImage);
			VirtualView.InternalCancel = new Action(CancelIfNeeded);
			VirtualView.InternalGetImageAsJPG = new Func<GetImageAsJpgArgs, Task<byte[]>>(GetImageAsJpgAsync);
			VirtualView.InternalGetImageAsPNG = new Func<GetImageAsPngArgs, Task<byte[]>>(GetImageAsPngAsync);

			UpdateAspect();
			UpdateImage(Control, VirtualView, null);

			base.ConnectHandler(platformView);
		}

		protected override void DisconnectHandler(Microsoft.UI.Xaml.Controls.Image platformView)
		{
			Control.ImageOpened -= OnImageOpened;
			VirtualView.PropertyChanged -= VirtualView_PropertyChanged;

			CancelIfNeeded();

			VirtualView.InternalReloadImage = null;
			VirtualView.InternalCancel = null;
			VirtualView.InternalGetImageAsJPG = null;
			VirtualView.InternalGetImageAsPNG = null;

			base.DisconnectHandler(platformView);
		}

		void VirtualView_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == CachedImage.SourceProperty.PropertyName)
			{
				UpdateImage(Control, VirtualView, null);
			}
			else if (e.PropertyName == CachedImage.AspectProperty.PropertyName)
			{
				UpdateAspect();
			}
		}

		public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        {
            var bitmapSource = Control.Source as BitmapSource;

            if (bitmapSource == null)
                return new SizeRequest();

            _measured = true;

            return new SizeRequest(new Size()
            {
                Width = bitmapSource.PixelWidth,
                Height = bitmapSource.PixelHeight
            });
        }

        void OnImageOpened(object sender, RoutedEventArgs routedEventArgs)
        {
            if (_measured)
            {
                ((IVisualElementController)VirtualView)?.InvalidateMeasure(Microsoft.Maui.Controls.Internals.InvalidationTrigger.MeasureChanged);
            }
        }

        async void UpdateImage(Microsoft.UI.Xaml.Controls.Image imageView, CachedImage image, CachedImage previousImage)
        {
            CancelIfNeeded();

            if (image == null || imageView == null || _isDisposed)
                return;

            var ffSource = await ImageSourceBinding.GetImageSourceBinding(image.Source, image).ConfigureAwait(false);
            if (ffSource == null)
            {
                if (_lastImageSource == null)
                    return;

                _lastImageSource = null;
                imageView.Source = null;
                return;
            }

            if (previousImage != null && !ffSource.Equals(_lastImageSource))
            {
                _lastImageSource = null;
                imageView.Source = null;
            }

            image.SetIsLoading(true);

            var placeholderSource = await ImageSourceBinding.GetImageSourceBinding(image.LoadingPlaceholder, image).ConfigureAwait(false);
            var errorPlaceholderSource = await ImageSourceBinding.GetImageSourceBinding(image.ErrorPlaceholder, image).ConfigureAwait(false);
            TaskParameter imageLoader;
            image.SetupOnBeforeImageLoading(out imageLoader, ffSource, placeholderSource, errorPlaceholderSource);

            if (imageLoader != null)
            {
                var finishAction = imageLoader.OnFinish;
                var sucessAction = imageLoader.OnSuccess;

                imageLoader.Finish((work) =>
                {
                    finishAction?.Invoke(work);
                    ImageLoadingSizeChanged(image, false);
                });

                imageLoader.Success((imageInformation, loadingResult) =>
                {
                    sucessAction?.Invoke(imageInformation, loadingResult);
                    _lastImageSource = ffSource;
                });

                imageLoader.LoadingPlaceholderSet(() => ImageLoadingSizeChanged(image, true));

                if (!_isDisposed)
                    _currentTask = imageLoader.Into(imageView);
            }
        }

        void UpdateAspect()
        {
            if (Control == null || VirtualView == null || _isDisposed)
                return;
            Control.Stretch = GetStretch(VirtualView.Aspect);
        }

        static Microsoft.UI.Xaml.Media.Stretch GetStretch(Aspect aspect)
        {
            switch (aspect)
            {
                case Aspect.AspectFill:
                    return Microsoft.UI.Xaml.Media.Stretch.UniformToFill;
                case Aspect.Fill:
                    return Microsoft.UI.Xaml.Media.Stretch.Fill;
                default:
                    return Microsoft.UI.Xaml.Media.Stretch.Uniform;
            }
        }

		private async void ImageLoadingSizeChanged(CachedImage element, bool isLoading)
		{
			if (element == null || _isDisposed)
				return;
				
			await ImageService.Instance.Config.MainThreadDispatcher.PostAsync(() =>
			{
				if (element == null || _isDisposed)
					return;

				((IVisualElementController)element).InvalidateMeasure(Microsoft.Maui.Controls.Internals.InvalidationTrigger.RendererReady);

				if (!isLoading)
					element.SetIsLoading(isLoading);
			}).ConfigureAwait(false);
		}

        void ReloadImage()
        {
            UpdateImage(Control, VirtualView, null);
        }

        void CancelIfNeeded()
        {
            try
            {
                _currentTask?.Cancel();
            }
            catch (Exception) { }
        }

        Task<byte[]> GetImageAsJpgAsync(GetImageAsJpgArgs args)
        {
            return GetImageAsByteAsync(BitmapEncoder.JpegEncoderId, args.Quality, args.DesiredWidth, args.DesiredHeight);
        }

        Task<byte[]> GetImageAsPngAsync(GetImageAsPngArgs args)
        {
            return GetImageAsByteAsync(BitmapEncoder.PngEncoderId, 90, args.DesiredWidth, args.DesiredHeight);
        }

        async Task<byte[]> GetImageAsByteAsync(Guid format, int quality, int desiredWidth, int desiredHeight)
        {
            if (Control == null || Control.Source == null)
                return null;

            var bitmap = Control.Source as WriteableBitmap;

            if (bitmap == null)
                return null;

            byte[] pixels = null;
            uint pixelsWidth = (uint)bitmap.PixelWidth;
            uint pixelsHeight = (uint)bitmap.PixelHeight;

            if (desiredWidth != 0 || desiredHeight != 0)
            {
                double widthRatio = (double)desiredWidth / (double)bitmap.PixelWidth;
                double heightRatio = (double)desiredHeight / (double)bitmap.PixelHeight;

                double scaleRatio = Math.Min(widthRatio, heightRatio);

                if (desiredWidth == 0)
                    scaleRatio = heightRatio;

                if (desiredHeight == 0)
                    scaleRatio = widthRatio;

                uint aspectWidth = (uint)((double)bitmap.PixelWidth * scaleRatio);
                uint aspectHeight = (uint)((double)bitmap.PixelHeight * scaleRatio);

                using (var tempStream = new InMemoryRandomAccessStream())
                {
                    byte[] tempPixels = await GetBytesFromBitmapAsync(bitmap);

                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, tempStream);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                        pixelsWidth, pixelsHeight, 96, 96, tempPixels);
                    await encoder.FlushAsync();
                    tempStream.Seek(0);

                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(tempStream);
                    BitmapTransform transform = new BitmapTransform()
                    {
                        ScaledWidth = aspectWidth,
                        ScaledHeight = aspectHeight,
                        InterpolationMode = BitmapInterpolationMode.Cubic
                    };
                    PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    pixels = pixelData.DetachPixelData();
                    pixelsWidth = aspectWidth;
                    pixelsHeight = aspectHeight;
                }
            }
            else
            {
                pixels = await GetBytesFromBitmapAsync(bitmap);
            }

            using (var stream = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder;

                if (format == BitmapEncoder.JpegEncoderId)
                {
                    var propertySet = new BitmapPropertySet();
                    var qualityValue = new BitmapTypedValue((double)quality / 100d, Windows.Foundation.PropertyType.Single);
                    propertySet.Add("ImageQuality", qualityValue);

                    encoder = await BitmapEncoder.CreateAsync(format, stream, propertySet);
                }
                else
                {
                    encoder = await BitmapEncoder.CreateAsync(format, stream);
                }

                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    pixelsWidth, pixelsHeight, 96, 96, pixels);
                await encoder.FlushAsync();
                stream.Seek(0);

                var bytes = new byte[stream.Size];
                await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);

                return bytes;
            }
        }

        async Task<byte[]> GetBytesFromBitmapAsync(WriteableBitmap bitmap)
        {
            byte[] tempPixels;
            using (var sourceStream = bitmap.PixelBuffer.AsStream())
            {
                tempPixels = new byte[sourceStream.Length];
                await sourceStream.ReadAsync(tempPixels, 0, tempPixels.Length).ConfigureAwait(false);
            }

            return tempPixels;
        }
    }
}
