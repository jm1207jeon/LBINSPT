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
        
        # OCR settings
        self.settings = {
            'word_spacing': 5,
            'line_spacing': 20,
            'min_confidence': 30,
            'image_scale_factor': 2.0,
            'enable_preprocessing': True,
            'small_text_enhancement': True
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
            print(f"Failed to initialize AWS TEXTRACT: {e}")
            self.aws_available = False
    
    def process_image(self, image):
        """Process image with AWS TEXTRACT OCR"""
        if self.processing:
            print("AWS TEXTRACT is already processing an image")
            return
            
        self.image = image
        self.start()
    
    def run(self):
        """Main OCR processing thread"""
        if self.image is None:
            return
            
        self.processing = True
        self.progress_updated.emit(10)
        
        try:
            print("Starting AWS TEXTRACT OCR processing...")
            
            # Preprocess image for better OCR results
            processed_image = self._preprocess_image(self.image)
            self.progress_updated.emit(30)
            
            # Convert image to bytes for AWS TEXTRACT
            image_bytes = self._image_to_bytes(processed_image)
            self.progress_updated.emit(50)
            
            # Call AWS TEXTRACT
            response = self.textract_client.detect_document_text(
                Document={'Bytes': image_bytes}
            )
            self.progress_updated.emit(80)
            
            # Convert results to standard format
            results = self._convert_textract_results(response)
            self.progress_updated.emit(90)
            
            # Apply minimal filtering (only parentheses)
            filtered_results = self._apply_filters(results)
            self.progress_updated.emit(100)
            
            print(f"AWS TEXTRACT completed: {len(filtered_results)} text items found")
            self.ocr_completed.emit(filtered_results)
            
        except Exception as e:
            print(f"AWS TEXTRACT OCR error: {e}")
            import traceback
            traceback.print_exc()
            self.ocr_completed.emit([])
        finally:
            self.processing = False
            self.progress_updated.emit(0)
    
    def _image_to_bytes(self, image):
        """Convert numpy image to bytes for AWS TEXTRACT"""
        # Convert to PIL Image
        if len(image.shape) == 3:
            pil_image = Image.fromarray(image)
        else:
            pil_image = Image.fromarray(cv2.cvtColor(image, cv2.COLOR_GRAY2RGB))
        
        # Convert to bytes
        img_byte_arr = io.BytesIO()
        pil_image.save(img_byte_arr, format='PNG')
        return img_byte_arr.getvalue()
    
    def _preprocess_image(self, image):
        """Preprocess image for better AWS TEXTRACT results"""
        if not self.settings.get('enable_preprocessing', True):
            return image
        
        # Convert to PIL for processing
        if len(image.shape) == 3:
            pil_image = Image.fromarray(image)
        else:
            pil_image = Image.fromarray(cv2.cvtColor(image, cv2.COLOR_GRAY2RGB))
        
        # Get original dimensions
        width, height = pil_image.size
        print(f"Original image size: {width}x{height}")
        
        # Scale up small images for better text recognition
        if width < 1200 or height < 1200:
            scale_factor = max(1200/width, 1200/height)
            new_width = int(width * scale_factor)
            new_height = int(height * scale_factor)
            pil_image = pil_image.resize((new_width, new_height), Image.Resampling.LANCZOS)
            print(f"Upscaled image from {width}x{height} to {new_width}x{new_height} for AWS TEXTRACT")
        
        # Apply slight enhancement for better text recognition
        from PIL import ImageEnhance
        
        # Enhance contrast slightly
        enhancer = ImageEnhance.Contrast(pil_image)
        pil_image = enhancer.enhance(1.1)
        
        # Sharpen for clearer text edges
        enhancer = ImageEnhance.Sharpness(pil_image)
        pil_image = enhancer.enhance(1.05)
        
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
                        print(f"Added text: '{text}' at bbox ({x}, {y}, {w}, {h}) confidence: {confidence:.1f}%")
            
            print(f"Converted {len(results)} text items from AWS TEXTRACT")
            
        except Exception as e:
            print(f"Error converting TEXTRACT results: {e}")
            import traceback
            traceback.print_exc()
        
        return results
    
    def _apply_filters(self, ocr_results):
        """Apply minimal filtering rules - only parentheses"""
        filtered_results = []
        
        for item in ocr_results:
            text = item['text']
            confidence = item['confidence']
            
            try:
                # Handle encoding issues with special characters more robustly
                safe_text = text.encode('utf-8', errors='replace').decode('utf-8')
                # Remove problematic Unicode characters
                safe_text = safe_text.replace('\u2014', '-').replace('\u2013', '-').replace('\xa9', '(C)')
                
                # Filter out text containing parentheses
                if '(' in safe_text or ')' in safe_text:
                    print(f"  Filtered out text with parentheses: '{safe_text}' (confidence: {confidence}%)")
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
    
    def extract_datamatrix_code(self, ocr_results):
        """Extract Data Matrix barcode content and parse application identifier (01)"""
        datamatrix_codes = []
        
        for result in ocr_results:
            text = result.get('text', '').strip()
            
            # Look for Data Matrix pattern - long alphanumeric strings
            if len(text) > 20 and any(char.isdigit() for char in text) and any(char.isalpha() for char in text):
                # Look for application identifier (01) followed by 14 digits
                match = re.search(r'01(\d{14})', text)
                if match:
                    gtin = match.group(1)
                    datamatrix_codes.append({
                        'full_text': text,
                        'gtin': gtin,
                        'bbox': result['bbox']
                    })
                    print(f"Found Data Matrix with GTIN: {gtin}")
        
        return datamatrix_codes
