"""
AWS TEXTRACT OCR Engine for Label Inspector
Provides OCR functionality using AWS TEXTRACT service
"""

import cv2
import numpy as np
import boto3
import base64
import io
import re
from PIL import Image
from PyQt5.QtCore import QThread, pyqtSignal
import time
import json

class AWSTextractEngine(QThread):
    ocr_completed = pyqtSignal(list)
    progress_updated = pyqtSignal(int)
    
    def __init__(self):
        super().__init__()
        self.image = None
        self.processing = False
        
        # AWS Configuration
        self.aws_region = 'ap-northeast-2'
        self.s3_bucket = 'mitechlabelinspector'
        self.application_tag = 'arn:aws:resource-groups:ap-northeast-2:851009250665:group/Textract/03ba96mua5c56wpodjuz5rms6g'
        
        # OCR settings - Enhanced for A3 portrait full coverage
        self.settings = {
            'word_spacing': 5,
            'line_spacing': 20,
            'min_confidence': 30,
            'image_scale_factor': 3.0,  # Increased for better A3 resolution
            'enable_preprocessing': True,
            'small_text_enhancement': True,
            'max_image_size': 10485760,  # 10MB limit for AWS Textract
            'resize_for_textract': True  # Enable resizing if image is too large
        }
        
        self._initialize_aws_clients()
    
    def _initialize_aws_clients(self):
        """Initialize AWS clients"""
        self.aws_available = False
        try:
            # Initialize AWS clients
            self.textract_client = boto3.client('textract', region_name=self.aws_region)
            self.s3_client = boto3.client('s3', region_name=self.aws_region)
            
            print("AWS TEXTRACT client initialized successfully")
            self.aws_available = True
            
        except Exception as e:
            print(f"AWS TEXTRACT initialization failed: {e}")
            print("Please configure AWS credentials and ensure proper IAM permissions")
            self.aws_available = False
    
    def process_image(self, image):
        """Process image with AWS TEXTRACT"""
        if self.processing:
            return
            
        if not self.aws_available:
            print("AWS TEXTRACT not initialized")
            return
            
        self.image = image
        self.start()
    
    def run(self):
        """Run OCR processing in separate thread"""
        if self.image is None:
            return
            
        self.processing = True
        self.progress_updated.emit(10)
        
        try:
            # Convert OpenCV image to bytes for AWS TEXTRACT
            self.progress_updated.emit(20)
            
            # Convert image to RGB if needed
            if len(self.image.shape) == 3:
                # Convert BGR to RGB
                rgb_image = cv2.cvtColor(self.image, cv2.COLOR_BGR2RGB)
            else:
                # Convert grayscale to RGB
                rgb_image = cv2.cvtColor(self.image, cv2.COLOR_GRAY2RGB)
            
            # Convert to PIL Image
            pil_image = Image.fromarray(rgb_image)
            
            # Optimize image for better OCR accuracy
            pil_image = self._optimize_image_for_textract(pil_image)
            
            self.progress_updated.emit(40)
            
            # Convert to bytes using JPEG compression for faster upload
            img_byte_arr = io.BytesIO()
            # Convert to RGB if image has transparency
            if pil_image.mode in ('RGBA', 'LA', 'P'):
                rgb_image = Image.new('RGB', pil_image.size, (255, 255, 255))
                rgb_image.paste(pil_image, mask=pil_image.split()[-1] if pil_image.mode == 'RGBA' else None)
                pil_image = rgb_image
            pil_image.save(img_byte_arr, format='JPEG', quality=85, optimize=True)
            img_bytes = img_byte_arr.getvalue()
            
            self.progress_updated.emit(50)
            
            # Call AWS TEXTRACT for text detection
            print("Calling AWS TEXTRACT for text detection...")
            
            response = self.textract_client.detect_document_text(
                Document={'Bytes': img_bytes}
            )
            
            # Also try to detect barcodes using a different approach
            # Since AWS Textract doesn't natively support barcode decoding,
            # we'll look for barcode-like patterns in the OCR text
            print("Analyzing OCR text for barcode patterns...")
            
            self.progress_updated.emit(80)
            
            # Convert TEXTRACT results to our format
            ocr_results = self._convert_textract_results(response)
            
            # Apply filtering rules (same as other OCR engines)
            filtered_results = self._apply_filters(ocr_results)
            
            self.progress_updated.emit(100)
            print(f"AWS TEXTRACT completed: {len(filtered_results)} text items found")
            
            self.ocr_completed.emit(filtered_results)
            
        except Exception as e:
            print(f"AWS TEXTRACT error: {e}")
            import traceback
            traceback.print_exc()
            self.ocr_completed.emit([])
        finally:
            self.processing = False
            self.progress_updated.emit(0)
    
    def _optimize_image_for_textract(self, pil_image):
        """Optimize image for AWS TEXTRACT processing"""
        width, height = pil_image.size
        
        # AWS TEXTRACT works best with images between 150-300 DPI
        # Resize if image is too large or too small
        if width > 2000 or height > 2000:
            # Downscale large images
            scale_factor = min(2000/width, 2000/height)
            new_width = int(width * scale_factor)
            new_height = int(height * scale_factor)
            pil_image = pil_image.resize((new_width, new_height), Image.Resampling.LANCZOS)
            print(f"Resized image from {width}x{height} to {new_width}x{new_height} for AWS TEXTRACT")
        # Scale up images with reduced requirements for faster processing
        elif width < 750 or height < 1000:  # 75% reduction from original 3000x4000
            # For portrait orientation
            if height > width:  # Portrait orientation
                scale_factor = max(750/width, 1000/height)
            else:  # Landscape orientation
                scale_factor = max(600/width, 600/height)
            new_width = int(width * scale_factor)
            new_height = int(height * scale_factor)
            pil_image = pil_image.resize((new_width, new_height), Image.Resampling.LANCZOS)
            print(f"Upscaled image from {width}x{height} to {new_width}x{new_height} for AWS TEXTRACT")
        
        # Skip image enhancement for faster processing
        # AWS TEXTRACT has built-in optimization
        
        return pil_image
    
    def _convert_textract_results(self, textract_response):
        """Convert AWS TEXTRACT response to our standard format"""
        results = []
        
        try:
            if 'Blocks' not in textract_response:
                print("No blocks found in TEXTRACT response")
                return results
            
            blocks = textract_response['Blocks']
            print(f"Found {len(blocks)} blocks in TEXTRACT response")
            
            # Process only WORD blocks to avoid duplicates (LINE blocks contain same text as WORD blocks)
            for block in blocks:
                # Only process WORD blocks that contain text (avoid LINE blocks to prevent duplicates)
                if block['BlockType'] == 'WORD' and 'Text' in block:
                    text = block['Text'].strip()
                    if not text:
                        continue
                    
                    # Get confidence score
                    confidence = block.get('Confidence', 0.0)
                    
                    # Get bounding box
                    if 'Geometry' in block and 'BoundingBox' in block['Geometry']:
                        bbox_data = block['Geometry']['BoundingBox']
                        
                        # TEXTRACT returns normalized coordinates (0-1)
                        # We need to convert them to pixel coordinates
                        # For now, we'll use a reference size and scale later
                        left = bbox_data['Left']
                        top = bbox_data['Top']
                        width = bbox_data['Width']
                        height = bbox_data['Height']
                        
                        # Convert to pixel coordinates (assuming image dimensions)
                        # We'll scale these based on the actual image size
                        img_width = getattr(self, '_current_image_width', 1000)
                        img_height = getattr(self, '_current_image_height', 1000)
                        
                        x = int(left * img_width)
                        y = int(top * img_height)
                        w = int(width * img_width)
                        h = int(height * img_height)
                        
                        result_item = {
                            'text': text,
                            'bbox': (x, y, w, h),
                            'confidence': int(confidence)
                        }
                        results.append(result_item)
                        try:
                            print(f"Added text: '{text}' at bbox ({x}, {y}, {w}, {h}) confidence: {confidence:.1f}%")
                        except UnicodeEncodeError:
                            print(f"Added text: [Unicode text] at bbox ({x}, {y}, {w}, {h}) confidence: {confidence:.1f}%")
            
            print(f"Converted {len(results)} text items from AWS TEXTRACT")
            
        except Exception as e:
            print(f"Error converting TEXTRACT results: {e}")
            import traceback
            traceback.print_exc()
        
        return results
    
    def _apply_filters(self, ocr_results):
        """Apply filtering rules - exclude application identifier patterns"""
        filtered_results = []
        
        for item in ocr_results:
            text = item['text']
            confidence = item['confidence']
            
            try:
                # Handle encoding issues with special characters more robustly
                safe_text = text.encode('utf-8', errors='replace').decode('utf-8')
                # Remove problematic Unicode characters
                safe_text = safe_text.replace('\u2014', '-').replace('\u2013', '-').replace('\xa9', '(C)')
                
                # Filter out texts that contain ONLY non-(01) application identifiers
                # Keep texts that contain (01) even if they have other identifiers
                if '(01)' not in safe_text:
                    # Filter out standalone (17), (10), etc. patterns
                    non_gtin_pattern = r'\((17|10|240|30|21)\)'
                    if re.search(non_gtin_pattern, safe_text):
                        print(f"  Filtered out non-GTIN application identifier: '{safe_text}' (confidence: {confidence}%)")
                        continue
                
                print(f"  Found text: '{safe_text}' (confidence: {confidence}%)")
                
                # Add to filtered results
                filtered_item = {
                    'text': safe_text,
                    'bbox': item['bbox'],
                    'confidence': confidence
                }
                filtered_results.append(filtered_item)
                
            except Exception as e:
                print(f"Error processing OCR result: {e}")
                continue
        
        return filtered_results
    
    def set_image_dimensions(self, width, height):
        """Set current image dimensions for coordinate scaling"""
        self._current_image_width = width
        self._current_image_height = height
    
    def is_processing(self):
        """Check if OCR is currently processing"""
        return self.processing
    
    def is_available(self):
        """Check if AWS TEXTRACT is available"""
        return getattr(self, 'aws_available', False)
    
    def apply_settings(self, settings):
        """Apply new settings to the OCR engine"""
        self.settings.update(settings)
        print(f"Applied AWS TEXTRACT settings: {settings}")
    
    def extract_gtin_from_text(self, ocr_results):
        """Extract GTIN from OCR text with (01) application identifier"""
        gtin_codes = []
        
        for result in ocr_results:
            text = result.get('text', '').strip()
            
            # Look for application identifier (01) followed by exactly 14 digits
            # This pattern handles continuous AI patterns like (01)08806367062567(10)25081095
            match_01 = re.search(r'\(01\)(\d{14})(?=\(|\s|$)', text)
            if match_01:
                gtin = match_01.group(1)
                
                # Calculate the exact position of the GTIN within the text
                gtin_start = match_01.start(1)  # Start of the GTIN digits (group 1)
                gtin_end = match_01.end(1)      # End of the GTIN digits
                
                print(f"Found GTIN pattern in text: '{text}' -> GTIN: {gtin}")
                
                # Calculate character-based position within the bounding box
                total_chars = len(text)
                if total_chars > 0:
                    bbox = result['bbox']
                    # Handle both tuple (x, y, w, h) and dict formats
                    if isinstance(bbox, tuple):
                        x, y, w, h = bbox
                        bbox_left = x
                        bbox_top = y
                        bbox_right = x + w
                        bbox_bottom = y + h
                    else:
                        bbox_left = bbox['left']
                        bbox_top = bbox['top']
                        bbox_right = bbox['right']
                        bbox_bottom = bbox['bottom']
                    
                    char_width = (bbox_right - bbox_left) / total_chars
                    
                    # Calculate GTIN-specific bounding box
                    gtin_left = bbox_left + (gtin_start * char_width)
                    gtin_right = bbox_left + (gtin_end * char_width)
                    
                    gtin_bbox = (int(gtin_left), int(bbox_top), int(gtin_right - gtin_left), int(bbox_bottom - bbox_top))
                else:
                    gtin_bbox = result['bbox']
                
                gtin_codes.append({
                    'full_text': text,
                    'gtin': gtin,
                    'bbox': gtin_bbox,
                    'gtin_text': gtin  # Store just the GTIN part for exact matching
                })
                print(f"Found GTIN with (01): {gtin} at position {gtin_start}-{gtin_end}")
        
        return gtin_codes
