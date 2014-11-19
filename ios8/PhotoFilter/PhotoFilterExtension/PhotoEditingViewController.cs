﻿using System;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Threading;

using Foundation;
using AVFoundation;
using PhotosUI;
using Photos;
using UIKit;
using CoreImage;
using CoreGraphics;
using CoreVideo;
using CoreMedia;

namespace PhotoFilterExtension
{
	public partial class PhotoEditingViewController : UIViewController, IPHContentEditingController, IUICollectionViewDelegate, IUICollectionViewDataSource, IVideoTransformer
	{
		const string kFilterInfoFilterNameKey = "filterName";
		const string kFilterInfoDisplayNameKey = "displayName";
		const string kFilterInfoPreviewImageKey = "previewImage";

		static readonly NSString PhotoFilterReuseId = new NSString ("PhotoFilterCell");

		PHContentEditingInput contentEditingInput;

		FilterInfo[] availableFilterInfos;
		string selectedFilterName;
		string initialFilterName;

		UIImage inputImage;
		CIFilter ciFilter;
		CIContext ciContext;

		string BundleId {
			get {
				return NSBundle.MainBundle.BundleIdentifier;
			}
		}

		public PhotoEditingViewController (IntPtr handle) : base (handle)
		{
			UIApplication.CheckForIllegalCrossThreadCalls = false;
		}

		public override void DidReceiveMemoryWarning ()
		{
			// Releases the view if it doesn't have a superview.
			base.DidReceiveMemoryWarning ();
		}

		public override void ViewDidLoad ()
		{
			base.ViewDidLoad ();
			Console.WriteLine ("Hello from Photo Extension");

			// Setup collection view
			CollectionView.AlwaysBounceHorizontal = true;
			CollectionView.AllowsMultipleSelection = false;
			CollectionView.AllowsSelection = true;

			FetchAvailableFilters ();

			ciContext = CIContext.FromOptions (null);

			// Add the background image and UIEffectView for the blur
			UIVisualEffectView effectView = new UIVisualEffectView (UIBlurEffect.FromStyle (UIBlurEffectStyle.Dark));
			effectView.TranslatesAutoresizingMaskIntoConstraints = false;
			View.InsertSubviewAbove (effectView, BackgroundImageView);

			View.AddConstraints (NSLayoutConstraint.FromVisualFormat ("V:|[effectView]|", (NSLayoutFormatOptions)0, "effectView", effectView));
			View.AddConstraints (NSLayoutConstraint.FromVisualFormat ("H:|[effectView]|", (NSLayoutFormatOptions)0, "effectView", effectView));
		}

		private void FetchAvailableFilters ()
		{
			// Load the available filters
			string plist = NSBundle.MainBundle.PathForResource ("Filters", "plist");
			var rawFiltersData = NSArray.FromFile (plist);

			int count = (int)rawFiltersData.Count;
			availableFilterInfos = new FilterInfo[count];

			for (int i = 0; i < count; i++)
				availableFilterInfos [i] = new FilterInfo (rawFiltersData.GetItem<NSDictionary> (i));
		}

		public override void ViewWillAppear (bool animated)
		{
			base.ViewWillAppear (animated);

			// Update the selection UI
			int index = Array.FindIndex (availableFilterInfos, fInfo => fInfo.FilterName == selectedFilterName);
			if (index < 0)
				return;

			NSIndexPath indexPath = NSIndexPath.FromItemSection (index, 0);
			CollectionView.SelectItem (indexPath, false, UICollectionViewScrollPosition.CenteredHorizontally);
			UpdateSelectionForCell (CollectionView.CellForItem (indexPath));
		}

		#region PHContentEditingController

		public bool CanHandleAdjustmentData (PHAdjustmentData adjustmentData)
		{
			// Inspect the adjustmentData to determine whether your extension can work with past edits.
			// (Typically, you use its formatIdentifier and formatVersion properties to do this.)
			bool result = adjustmentData.FormatIdentifier == BundleId;
			result &= adjustmentData.FormatVersion == "1.0";
			return result;
		}

		public void StartContentEditing (PHContentEditingInput input, UIImage placeholderImage)
		{
			// Present content for editing and keep the contentEditingInput for use when closing the edit session.
			// If you returned true from CanHandleAdjustmentData(), contentEditingInput has the original image and adjustment data.
			// If you returned false, the contentEditingInput has past edits "baked in".
			contentEditingInput = input;

			// Load input image
			switch (contentEditingInput.MediaType) {
				case PHAssetMediaType.Image:
					inputImage = contentEditingInput.DisplaySizeImage;
					break;

				case PHAssetMediaType.Video:
					inputImage = ImageFor (contentEditingInput.AvAsset, 0);
					break;

				default:
					break;
			}

			// Load adjustment data, if any
			try {
				PHAdjustmentData adjustmentData = contentEditingInput.AdjustmentData;
				if (adjustmentData != null)
					selectedFilterName = (string)(NSString)NSKeyedUnarchiver.UnarchiveObject (adjustmentData.Data);
			} catch (Exception exception) {
				Console.WriteLine ("Exception decoding adjustment data: {0}", exception);
			}

			if (string.IsNullOrWhiteSpace (selectedFilterName))
				selectedFilterName = "CISepiaTone";

			initialFilterName = selectedFilterName;

			// Update filter and background image
			UpdateFilter ();
			UpdateFilterPreview ();
			BackgroundImageView.Image = placeholderImage;
		}

		public void FinishContentEditing (Action<PHContentEditingOutput> completionHandler)
		{
			PHContentEditingOutput contentEditingOutput = new PHContentEditingOutput (contentEditingInput);

			// Adjustment data
			NSData archivedData = NSKeyedArchiver.ArchivedDataWithRootObject ((NSString)selectedFilterName);
			PHAdjustmentData adjustmentData = new PHAdjustmentData (BundleId, "1.0", archivedData);
			contentEditingOutput.AdjustmentData = adjustmentData;

			switch (contentEditingInput.MediaType) {
				case PHAssetMediaType.Image:
					{
						// Get full size image
						NSUrl url = contentEditingInput.FullSizeImageUrl;
						CIImageOrientation orientation = contentEditingInput.FullSizeImageOrientation;

						// Generate rendered JPEG data
						UIImage image = UIImage.FromFile (url.Path);
						image = TransformImage (image, orientation);
						NSData renderedJPEGData = image.AsJPEG (0.9f);

						// Save JPEG data
						NSError error = null;
						bool success = renderedJPEGData.Save (contentEditingOutput.RenderedContentUrl, NSDataWritingOptions.Atomic, out error);
						if (success) {
							completionHandler (contentEditingOutput);
						} else {
							Console.WriteLine ("An error occured: {0}", error);
							completionHandler (null);
						}
						break;
					}

				case PHAssetMediaType.Video:
					{
						AVReaderWriter avReaderWriter = new AVReaderWriter (contentEditingInput.AvAsset, this);

						// Save filtered video
						avReaderWriter.WriteToUrl (contentEditingOutput.RenderedContentUrl, error => {
							if (error == null) {
								completionHandler (contentEditingOutput);
								return;
							}
							Console.WriteLine ("An error occured: {0}", error);
							completionHandler (null);
						});
						break;
					}

				default:
					break;
			}
		}

		public void CancelContentEditing ()
		{
			// Clean up temporary files, etc.
			// May be called after finishContentEditingWithCompletionHandler: while you prepare output.
		}

		public bool ShouldShowCancelConfirmation {
			get {
				var shouldShowCancelConfirmation = false;

				if (selectedFilterName != initialFilterName) {
					shouldShowCancelConfirmation = true;
				}

				return shouldShowCancelConfirmation;
			}
		}

		#endregion

		#region Image Filtering

		private void UpdateFilter ()
		{
			ciFilter = CIFilter.FromName (selectedFilterName);

			var inputImage = CIImage.FromCGImage (this.inputImage.CGImage);
			CIImageOrientation orientation = Convert (this.inputImage.Orientation);
			inputImage = inputImage.CreateWithOrientation (orientation);

			ciFilter.Image = inputImage;
		}

		private void UpdateFilterPreview ()
		{
			CIImage outputImage = ciFilter.OutputImage;

			UIImage transformedImage;
			using (CGImage cgImage = ciContext.CreateCGImage (outputImage, outputImage.Extent))
				transformedImage = UIImage.FromImage (cgImage);

			FilterPreviewView.Image = transformedImage;
		}

		private UIImage TransformImage (UIImage image, CIImageOrientation orientation)
		{
			CIImage inputImage = CIImage.FromCGImage (image.CGImage);
			inputImage = inputImage.CreateWithOrientation (orientation);

			ciFilter.SetValueForKey (inputImage, CIFilterInputKey.Image);
			CIImage outputImage = ciFilter.OutputImage;

			using (CGImage cgImage = ciContext.CreateCGImage (outputImage, outputImage.Extent))
				return UIImage.FromImage (cgImage);
		}

		#endregion

		#region Video Filtering

		public void AdjustPixelBuffer (CVPixelBuffer inputBuffer, CVPixelBuffer outputBuffer)
		{
			using (CIImage img = CIImage.FromImageBuffer (inputBuffer)) {
				ciFilter.Image = img;
				using (var outImg = ciFilter.OutputImage)
					ciContext.Render (outImg, outputBuffer);
			}
		}

		#endregion

		#region UICollectionViewDataSource & UICollectionViewDelegate

		public nint GetItemsCount (UICollectionView collectionView, nint section)
		{
			return availableFilterInfos.Length;
		}

		public UICollectionViewCell GetCell (UICollectionView collectionView, NSIndexPath indexPath)
		{
			var filterInfo = availableFilterInfos [indexPath.Item];

			var cell = (UICollectionViewCell)collectionView.DequeueReusableCell (PhotoFilterReuseId, indexPath);

			var imageView = (UIImageView)cell.ViewWithTag (999);
			imageView.Image = UIImage.FromBundle (filterInfo.PreviewImage);

			UILabel label = (UILabel)cell.ViewWithTag (998);
			label.Text = filterInfo.DisplayName;

			UpdateSelectionForCell (cell);
			return cell;
		}

		[Export ("collectionView:didSelectItemAtIndexPath:")]
		public void ItemSelected (UICollectionView collectionView, NSIndexPath indexPath)
		{
			selectedFilterName = availableFilterInfos [indexPath.Item].FilterName;
			UpdateFilter ();

			UpdateSelectionForCell (collectionView.CellForItem (indexPath));

			UpdateFilterPreview ();
		}

		[Export ("collectionView:didDeselectItemAtIndexPath:")]
		public void ItemDeselected (UICollectionView collectionView, NSIndexPath indexPath)
		{
			UpdateSelectionForCell (collectionView.CellForItem (indexPath));
		}

		private void UpdateSelectionForCell (UICollectionViewCell cell)
		{
			if (cell == null)
				return;

			bool isSelected = cell.Selected;

			UIImageView imageView = (UIImageView)cell.ViewWithTag (999);
			imageView.Layer.BorderColor = View.TintColor.CGColor;
			imageView.Layer.BorderWidth = isSelected ? 2 : 0;

			UILabel label = (UILabel)cell.ViewWithTag (998);
			label.TextColor = isSelected ? View.TintColor : UIColor.White;
		}

		#endregion

		#region Utilities

		// Returns the EXIF/TIFF orientation value corresponding to the given UIImageOrientation value.
		private CIImageOrientation Convert (UIImageOrientation imageOrientation)
		{
			switch (imageOrientation) {
				case  UIImageOrientation.Up:
					return CIImageOrientation.TopLeft;
				case UIImageOrientation.Down:
					return CIImageOrientation.BottomRight;
				case UIImageOrientation.Left:
					return CIImageOrientation.LeftBottom;
				case UIImageOrientation.Right:
					return CIImageOrientation.RightTop;
				case UIImageOrientation.UpMirrored:
					return CIImageOrientation.TopRight;
				case UIImageOrientation.DownMirrored:
					return CIImageOrientation.BottomLeft;
				case UIImageOrientation.LeftMirrored:
					return CIImageOrientation.LeftTop;
				case UIImageOrientation.RightMirrored:
					return CIImageOrientation.RightBottom;
				default:
					throw new NotImplementedException ();
			}
		}

		private UIImage ImageFor (AVAsset avAsset, double time)
		{
			AVAssetImageGenerator imageGenerator = AVAssetImageGenerator.FromAsset (avAsset);
			imageGenerator.AppliesPreferredTrackTransform = true;

			CMTime actualTime;
			NSError error = null;
			var requestedTime = new CMTime ((long)time, 100);
			using (CGImage posterImage = imageGenerator.CopyCGImageAtTime (requestedTime, out actualTime, out error))
				return UIImage.FromImage (posterImage);
		}

		#endregion
	}
}

