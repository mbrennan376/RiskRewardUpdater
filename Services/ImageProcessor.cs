using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using System;
using System.Drawing;
using System.Linq;

public static class ImageProcessor
{
    public static void RemoveVideoBox(string inputImagePath, string outputImagePath)
    {
        // Load the image
        using (Mat image = CvInvoke.Imread(inputImagePath, ImreadModes.Color))
        {
            // Detect the video box area
            Rectangle boxArea = DetectVideoBox(image);

            // Overlay the black box with soft edges
            if (boxArea != Rectangle.Empty)
            {
                OverlayBlackBox(image, boxArea);
            }

            // Save the output image
            CvInvoke.Imwrite(outputImagePath, image);
        }
    }

    private static Rectangle DetectVideoBox(Mat image)
    {
        // Define the region of interest (ROI) in the lower-left corner
        // We'll use a more conservative size to avoid missing the box
        int roiHeight = image.Height / 2;  // Look at the lower half
        int roiWidth = image.Width / 3;    // Look at the left third of the image
        Rectangle roi = new Rectangle(0, image.Height - roiHeight, roiWidth, roiHeight);

        // Extract the region of interest
        Mat roiMat = new Mat(image, roi);

        // Convert the cropped region of interest to grayscale
        Mat gray = new Mat();
        CvInvoke.CvtColor(roiMat, gray, ColorConversion.Bgr2Gray);

        // Apply adaptive thresholding to find the non-black areas
        Mat thresh = new Mat();
        CvInvoke.Threshold(gray, thresh, 50, 255, ThresholdType.BinaryInv);

        // Find contours in the thresholded image
        using (var contours = new VectorOfVectorOfPoint())
        {
            CvInvoke.FindContours(thresh, contours, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);

            if (contours.Size > 0)
            {
                // Get the largest contour, assuming it's the box
                VectorOfPoint largestContour = null;
                double maxArea = 0;

                for (int i = 0; i < contours.Size; i++)
                {
                    VectorOfPoint contour = contours[i];
                    double contourArea = CvInvoke.ContourArea(contour);

                    // Consider only larger contours to avoid noise
                    if (contourArea > 500 && contourArea > maxArea)
                    {
                        largestContour = contour;
                        maxArea = contourArea;
                    }
                }

                if (largestContour != null)
                {
                    // Get the bounding rectangle of the largest contour
                    Rectangle boundingRect = CvInvoke.BoundingRectangle(largestContour);

                    // Adjust the bounding rectangle to match the position in the original image
                    boundingRect.X += roi.X;  // Shift to the original image's coordinate system
                    boundingRect.Y += roi.Y;

                    // Inflate the box slightly to ensure full coverage
                    boundingRect.Inflate(10, 10);  // Adjust this value if necessary

                    return boundingRect;
                }
            }
        }

        // If no box is detected, return a fallback rectangle (estimate in lower-left corner)
        return new Rectangle(0, image.Height - roiHeight, roiWidth, roiHeight);
    }

    private static void OverlayBlackBox(Mat image, Rectangle boxArea)
    {
        if (boxArea != Rectangle.Empty)
        {
            // Create a mask for the area to blur the edges
            Mat mask = new Mat(image.Size, DepthType.Cv8U, 1);
            mask.SetTo(new MCvScalar(0));
            CvInvoke.Rectangle(mask, boxArea, new MCvScalar(255), -1);

            // Apply Gaussian blur for softer edges
            CvInvoke.GaussianBlur(mask, mask, new Size(45, 45), 0);

            // Create a black overlay
            Mat blackOverlay = new Mat(image.Size, DepthType.Cv8U, 3);
            blackOverlay.SetTo(new MCvScalar(0, 0, 0));

            // Apply the black overlay using the mask
            blackOverlay.CopyTo(image, mask);
        }
    }






}
