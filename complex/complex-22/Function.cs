using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.Json;
using System;
using System.Threading.Tasks;
using System.Collections;

using System.Collections.Generic;
using System.IO;
using System.Linq;

using Amazon.Lambda.S3Events;

using Amazon.Rekognition;
using Amazon.Rekognition.Model;

using Amazon.S3;
using Amazon.S3.Model;
using Amazon.XRay.Recorder.Core;

namespace CustomRuntimeFunction
{
    public class Function
    {
        private static async Task Main(string[] args)
        {
            foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            {
                Console.WriteLine(e.Key + ":" + e.Value);
            }

            Func<S3Event, ILambdaContext, Task<S3Event>> func = FunctionHandler;
            using (var handlerWrapper = HandlerWrapper.GetHandlerWrapper(func, new JsonSerializer()))
            using (var bootstrap = new LambdaBootstrap(handlerWrapper))
            {
                await bootstrap.RunAsync();
            }
        }

        public static float DEFAULT_MIN_CONFIDENCE = 70f;
        public static string MIN_CONFIDENCE_ENVIRONMENT_VARIABLE_NAME = "MinConfidence";
        static IAmazonS3 S3Client { get; }
        static IAmazonRekognition RekognitionClient { get; }
        static float MinConfidence { get; set; } = DEFAULT_MIN_CONFIDENCE;
        static HashSet<string> SupportedImageTypes { get; } = new HashSet<string> { ".png", ".jpg", ".jpeg" };

        static Function()
        {
            S3Client = new AmazonS3Client();
            RekognitionClient = new AmazonRekognitionClient();

            var environmentMinConfidence = System.Environment.GetEnvironmentVariable(MIN_CONFIDENCE_ENVIRONMENT_VARIABLE_NAME);
            if (!string.IsNullOrWhiteSpace(environmentMinConfidence))
            {
                float value;
                if (float.TryParse(environmentMinConfidence, out value))
                {
                    MinConfidence = value;
                    Console.WriteLine($"Setting minimum confidence to {MinConfidence}");
                }
                else
                {
                    Console.WriteLine($"Failed to parse value {environmentMinConfidence} for minimum confidence. Reverting back to default of {MinConfidence}");
                }
            }
            else
            {
                Console.WriteLine($"Using default minimum confidence of {MinConfidence}");
            }
        }

        public static async Task<S3Event> FunctionHandler(S3Event input, ILambdaContext context)
        {
            var tags = new List<Tag>();
            var record = input?.Records?.FirstOrDefault();

            if (record == null)
            {
                Console.WriteLine("Input does not contain an S3 record.");
                throw new Exception();
            }

            record.S3.Object.Key = "skateboard_resized2.jpg";

            if (!SupportedImageTypes.Contains(Path.GetExtension(record.S3.Object.Key)))
            {
                Console.WriteLine($"Object {record.S3.Bucket.Name}:{record.S3.Object.Key} is not a supported image type");
                throw new Exception();
            }

            Console.WriteLine($"Looking for labels in image {record.S3.Bucket.Name}:{record.S3.Object.Key}");

            AWSXRayRecorder.Instance.BeginSubsegment("RekognitionDetectLabels");
            var detectResponses = await RekognitionClient.DetectLabelsAsync(new DetectLabelsRequest
            {
                MinConfidence = MinConfidence,
                Image = new Image
                {
                    S3Object = new Amazon.Rekognition.Model.S3Object
                    {
                        Bucket = record.S3.Bucket.Name,
                        Name = record.S3.Object.Key
                    }
                }
            });
            AWSXRayRecorder.Instance.EndSubsegment();

            foreach (var label in detectResponses.Labels)
            {
                if (tags.Count < 10)
                {
                    Console.WriteLine($"\tFound Label {label.Name} with confidence {label.Confidence}");
                    tags.Add(new Tag { Key = label.Name, Value = label.Confidence.ToString() });
                }
                else
                {
                    Console.WriteLine($"\tSkipped label {label.Name} with confidence {label.Confidence} because the maximum number of tags has been reached");
                }
            }

            AWSXRayRecorder.Instance.BeginSubsegment("S3PutObjectTagging");
            await S3Client.PutObjectTaggingAsync(new PutObjectTaggingRequest
            {
                BucketName = record.S3.Bucket.Name,
                Key = record.S3.Object.Key,
                Tagging = new Tagging
                {
                    TagSet = tags
                }
            });
            AWSXRayRecorder.Instance.EndSubsegment();

            AWSXRayRecorder.Instance.BeginSubsegment("S3DeleteObjectTagging");
            await S3Client.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest
            {
                BucketName = record.S3.Bucket.Name,
                Key = record.S3.Object.Key
            });
            AWSXRayRecorder.Instance.EndSubsegment();

            return input;
        }
    }
}
