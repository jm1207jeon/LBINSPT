import cv2
import threading
import time
from PyQt5.QtCore import QObject, pyqtSignal

class CameraHandler(QObject):
    frame_ready = pyqtSignal(object)
    
    def __init__(self):
        super().__init__()
        self.camera = None
        self.current_camera_index = 0
        self.is_running = False
        self.current_frame = None
        self.frame_lock = threading.Lock()
        self.capture_thread = None
        
    def get_available_cameras(self):
        """Get list of available camera indices, prioritizing USB cameras"""
        available_cameras = []
        usb_cameras = []
        built_in_cameras = []
        
        for i in range(10):  # Check first 10 camera indices
            cap = cv2.VideoCapture(i)
            if cap.isOpened():
                # Try to get camera name/info to distinguish USB vs built-in
                # USB cameras typically have higher indices or different properties
                if i > 0:  # Assume index 0 is built-in, others are USB
                    usb_cameras.append(i)
                else:
                    built_in_cameras.append(i)
                cap.release()
        
        # Prioritize USB cameras first, then built-in
        available_cameras = usb_cameras + built_in_cameras
        return available_cameras if available_cameras else [0]  # Fallback to index 0
        
    def set_camera(self, camera_index):
        """Set the camera to use"""
        self.current_camera_index = camera_index
        
    def start_camera(self):
        """Start camera capture"""
        try:
            self.camera = cv2.VideoCapture(self.current_camera_index)
            if not self.camera.isOpened():
                return False
                
            # Set camera properties for better performance
            self.camera.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
            self.camera.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
            self.camera.set(cv2.CAP_PROP_FPS, 30)
            
            self.is_running = True
            self.capture_thread = threading.Thread(target=self._capture_frames)
            self.capture_thread.daemon = True
            self.capture_thread.start()
            
            return True
        except Exception as e:
            print(f"Error starting camera: {e}")
            return False
            
    def stop_camera(self):
        """Stop camera capture"""
        self.is_running = False
        if self.capture_thread:
            self.capture_thread.join(timeout=1.0)
            
        if self.camera:
            self.camera.release()
            self.camera = None
            
    def _capture_frames(self):
        """Capture frames in separate thread"""
        while self.is_running and self.camera:
            ret, frame = self.camera.read()
            if ret:
                # Convert BGR to RGB for Qt display
                frame_rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                
                with self.frame_lock:
                    self.current_frame = frame_rgb
                    
                self.frame_ready.emit(frame_rgb)
            else:
                time.sleep(0.01)  # Small delay if frame read fails
                
    def get_frame(self):
        """Get the most recent frame"""
        with self.frame_lock:
            return self.current_frame.copy() if self.current_frame is not None else None
