using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Forms;
using nucs.Automation;

namespace KasraFbGamesTest
{
    public partial class Form1 : Form
    {
        private DirectXManager _dxManager;
        
        // vvv You can edit these vvv
        // A fixed amount to move the mouse vertically each throw, you could make this into a MIN and MAX to do variable amounts in a range.
        private int FLICK_MOVEMENT_Y = 40;
        // The max movement to use for the horizontal motion of each throw, how far should the horizontal movement be when the net is on the far-left or far-right?
        private int FLICK_MOVEMENT_X_MAX = 30;
        // How big of an area to look around of the detected ball and net
        private int CROP_IMG_MARGIN = 200;

        // Game specific variables
        private Rect _ballLastRect;
        private Rect _netLastRect;
        private int _lastNetXDelta;
        private bool _inGame;
        private int _attemptsMadeThisGame;
        private DateTime _lastFlickTime;

        public Form1()
        {
            InitializeComponent();
            _dxManager = new DirectXManager();
        }

        private async Task<bool> FlickBall(Rect ball, Rect net)
        {
            BackColor = Color.HotPink;

            int ballMiddleX = (int)ball.centerX();
            int ballMiddleY = (int)ball.centerY();

            int netMiddleX = (int)net.centerX();
            int netMiddleY = (int)net.centerY();

            int diffX = netMiddleX - ballMiddleX;
            double diffScaled = diffX / (ball.width() * 1.0);

            int maxXMovement = diffScaled > 0.0f ? FLICK_MOVEMENT_X_MAX : -FLICK_MOVEMENT_X_MAX;
            int movementX = Math.Abs(diffScaled) > 0.1f ? 0 + (int)(maxXMovement * SineEaseInOut(diffScaled * 0.65)) : 0;

            Debug.WriteLine("Shooting: ballMiddleX: {0}, netMiddleX: {1}, diffX: {2}, diffScaled: {3}, movementX: {4}", ballMiddleX, netMiddleX, diffX, diffScaled, movementX);

            Mouse.AbsoluteMove(ballMiddleX, ballMiddleY);
            Mouse.LeftDown();
            await Mouse.MoveRelative(movementX, -FLICK_MOVEMENT_Y);
            Mouse.LeftUp();

            BackColor = DefaultBackColor;
            _lastFlickTime = DateTime.UtcNow;

            return true;
        }
        
        private void Form1_Load(object sender, EventArgs e)
        {
            _dxManager.Init(Handle);
            _dxManager.StartRecording(() => onFrameUpdate());
        }

        private async void onFrameUpdate()
        {
            var update = _dxManager.GetProcessor().Take();

            Bitmap img = (Bitmap)update.LastAcquiredFrame.Clone();
            img.Save("last_frame.jpg", ImageFormat.Jpeg);

            Process compiler = new Process();
            compiler.StartInfo.FileName = "custom_object_detector";
            compiler.StartInfo.Arguments = ". last_frame.jpg";
            compiler.StartInfo.UseShellExecute = false;
            compiler.StartInfo.RedirectStandardOutput = true;
            compiler.StartInfo.CreateNoWindow = true;
            compiler.Start();

            var output = compiler.StandardOutput.ReadToEnd();

            compiler.WaitForExit();
            
            if (compiler.ExitCode == 0 && output.StartsWith("{"))
            {
                var res = Jil.JSON.Deserialize<DetectorResponse>(output);

                Rect ball = res.ball;
                Rect net = res.net;

                var farLeft = net.left < ball.left ? net.left : ball.left;
                var farRight = net.right > ball.right ? net.right : ball.right;

                var cropLeft = farLeft < CROP_IMG_MARGIN ? farLeft : farLeft - CROP_IMG_MARGIN;
                var cropRight = farRight > img.Width - CROP_IMG_MARGIN ? farRight : farRight + CROP_IMG_MARGIN;

                var cropTop = net.top < CROP_IMG_MARGIN ? net.top : net.top - CROP_IMG_MARGIN;
                var cropBottom = ball.bottom > img.Height - CROP_IMG_MARGIN ? ball.bottom : ball.bottom + CROP_IMG_MARGIN;

                var cropRectangle = new Rectangle(cropLeft, cropTop, cropRight - cropLeft, cropBottom - cropTop);
                var croppedBitmap = img.Clone(cropRectangle, img.PixelFormat);

                if (!_inGame)
                {
                    _inGame = true;
                    _attemptsMadeThisGame = 0;
                }

                var timeSinceLast = DateTime.UtcNow.Subtract(_lastFlickTime);

                var lastBallXMovement = 0;
                if (_ballLastRect != null)
                    lastBallXMovement = Math.Abs(_ballLastRect.left - ball.left);

                var lastNetXDelta = 0;
                if (_netLastRect != null)
                    lastNetXDelta = net.left - _netLastRect.left;

                var isNetMovingStage = _inGame && _attemptsMadeThisGame > 10;
                if (!isNetMovingStage && (lastNetXDelta != 0 && _lastNetXDelta != 0))
                    isNetMovingStage = true;

                var isNetMovingOverride = false;
                if (isNetMovingStage)
                {
                    int boundsStart;
                    int boundsEnd;
                    int comparePoint;

                    if (lastNetXDelta > 0)
                    {
                        // net is moving to the right
                        boundsStart = (int)(ball.left - (ball.width() * 3.0));
                        boundsEnd = ball.left;
                        comparePoint = (int)net.centerX();
                    } else
                    {
                        // net is moving to the left
                        boundsStart = ball.right;
                        boundsEnd = (int)(ball.right + (ball.width() * 3.0));
                        comparePoint = (int)net.centerX();
                    }

                    Debug.WriteLine("\tisNetMovingStage, compareX: {0}, boundsStart: {1}, boundsEnd: {2}", comparePoint, boundsStart, boundsEnd);
                }

                var ballDiffToNet = ball.bottom - net.bottom;

                var timeBeforeNextShot = isNetMovingStage ? 5000 : 3000;

                // Only if we haven't done anything in at least timeBeforeNextShot milliseconds, and the ball hasn't moved much since last frame, and the net hasn't moved (or it's in the moving stage and we can shoot)
                if (timeSinceLast.TotalMilliseconds > timeBeforeNextShot && lastBallXMovement < 5 && ballDiffToNet > 100 && (lastNetXDelta == 0 || (isNetMovingStage || isNetMovingOverride)))
                {
                    var destination = net;
                    if (isNetMovingOverride)
                    {
                        destination = new Rect();
                        destination.left = ball.left;
                        destination.right = ball.right;
                    }
                    // To update the GUI
                    pictureBox1.Image = croppedBitmap;
                    // To actually flick the mouse as needed and wait until it's done
                    await FlickBall(ball, destination);
                } else
                {
                    Debug.WriteLine("Not updating, timeSinceLast: {0}, lastBalLXDelta: {1}, lastNetXDelta:  {2}, isNetMovingStage: {3}", timeSinceLast.TotalMilliseconds, lastBallXMovement, lastNetXDelta, isNetMovingStage);
                }

                _lastNetXDelta = lastNetXDelta;
                _netLastRect = net;
                _ballLastRect = ball;
            }
            else
            {
                if (_inGame)
                {
                    _inGame = false;
                    _attemptsMadeThisGame = 0;
                }
            }

            await Task.Delay(50);
            _dxManager.GetRecorder().Add(update);

            img.Dispose();
        }

        public static float SineEaseInOut(double s)
        {
            return (float)(Math.Sin(s * (float)Math.PI - (float)(Math.PI / 2)) + 1) / 2;
        }

        private class Rect
        {
            public int left { get; set; }
            public int top { get; set; }
            public int right { get; set; }
            public int bottom { get; set; }

            public int width()
            {
                return right - left;
            }

            public int height()
            {
                return bottom - top;
            }

            public double centerX()
            {
                return left + ((right - left) / 2.0);
            }

            public double centerY()
            {
                return top + ((bottom - top) / 2.0);
            }
        }

        private class DetectorResponse {
            public Rect ball { get; set; }
            public Rect net { get; set; }
        }
    }
}
