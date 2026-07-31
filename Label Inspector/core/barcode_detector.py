import cv2
import numpy as np
import re
from PyQt5.QtCore import QObject

# Try to import pyzbar, fall back to alternative if not available
try:
    from pyzbar import pyzbar
    PYZBAR_AVAILABLE = True
except ImportError:
    print("Warning: pyzbar not available. Barcode detection will be limited.")
    PYZBAR_AVAILABLE = False

class BarcodeDetector(QObject):
    def __init__(self):
        super().__init__()
        
    def detect_barcodes(self, image):
        """Detect all types of barcodes including Data Matrix and GS1-128"""
        results = []
        
        try:
            # Convert to grayscale if needed
            if len(image.shape) == 3:
                gray = cv2.cvtColor(image, cv2.COLOR_RGB2GRAY)
            else:
                gray = image.copy()
            
            # Detect all barcodes using pyzbar (supports many formats)
            barcodes = []
            if PYZBAR_AVAILABLE:
                barcodes = pyzbar.decode(gray)
            
            for barcode in barcodes:
                # Extract barcode data
                barcode_data = barcode.data.decode('utf-8')
                barcode_type = barcode.type
                
                # Get bounding box
                (x, y, w, h) = barcode.rect
                
                # Parse GS1 data if applicable
                parsed_data = self._parse_gs1_data(barcode_data)
                
                results.append({
                    'type': barcode_type,
                    'data': barcode_data,
                    'parsed_data': parsed_data,
                    'bbox': (x, y, w, h)
                })
            
            # Additional detection methods for better coverage
            results.extend(self._detect_datamatrix_opencv(gray))
            results.extend(self._detect_with_preprocessing(gray))
            
        except Exception as e:
            print(f"Barcode detection error: {e}")
            
        return results
    
    def _detect_datamatrix_opencv(self, gray_image):
        """Detect Data Matrix codes using OpenCV"""
        results = []
        
        try:
            # Create Data Matrix detector
            detector = cv2.QRCodeDetector()
            
            # Detect and decode
            retval, decoded_info, points, straight_qrcode = detector.detectAndDecodeMulti(gray_image)
            
            if retval:
                for i, info in enumerate(decoded_info):
                    if info:  # If decoding was successful
                        # Get bounding box from points
                        if points is not None and len(points) > i:
                            pts = points[i].reshape((-1, 2)).astype(int)
                            x, y, w, h = cv2.boundingRect(pts)
                            
                            parsed_data = self._parse_gs1_data(info)
                            
                            results.append({
                                'type': 'DATAMATRIX_CV',
                                'data': info,
                                'parsed_data': parsed_data,
                                'bbox': (x, y, w, h)
                            })
                            
        except Exception as e:
            print(f"OpenCV Data Matrix detection error: {e}")
            
        return results
    
    def _parse_gs1_data(self, data):
        """Parse GS1 application identifier data"""
        parsed = {}
        
        try:
            # Common GS1 Application Identifiers
            gs1_patterns = {
                '01': 'GTIN',           # Global Trade Item Number
                '10': 'LOT',            # Batch/Lot Number
                '11': 'MFG_DATE',       # Production Date
                '17': 'EXP_DATE',       # Expiration Date
                '21': 'SERIAL',         # Serial Number
                '240': 'ADDITIONAL_ID', # Additional Product Identification
                '241': 'CUSTOMER_PART', # Customer Part Number
                '242': 'MTO_VARIANT',   # Made-to-Order Variation
                '243': 'PCN',           # Packaging Component Number
                '250': 'SECONDARY_SERIAL', # Secondary Serial Number
                '251': 'REF',           # Reference to Source Entity
                '30': 'COUNT'           # Variable Count
            }
            
            # Look for GS1 format (starts with application identifier)
            if data.startswith('01') or any(data.startswith(ai) for ai in gs1_patterns.keys()):
                i = 0
                while i < len(data):
                    # Try to find application identifier
                    found_ai = None
                    for ai in sorted(gs1_patterns.keys(), key=len, reverse=True):
                        if data[i:].startswith(ai):
                            found_ai = ai
                            break
                    
                    if found_ai:
                        i += len(found_ai)
                        # Extract data until next AI or end
                        value_start = i
                        
                        # Find next AI or end of string
                        next_ai_pos = len(data)
                        for next_ai in gs1_patterns.keys():
                            pos = data.find(next_ai, i)
                            if pos != -1 and pos < next_ai_pos:
                                next_ai_pos = pos
                        
                        value = data[value_start:next_ai_pos]
                        parsed[gs1_patterns[found_ai]] = value
                        i = next_ai_pos
                    else:
                        break
            
            # If no GS1 format detected, try to extract common patterns
            if not parsed:
                # Look for date patterns
                date_matches = re.findall(r'\d{6}|\d{8}', data)
                if date_matches:
                    # Assume first date is MFG, second is EXP
                    if len(date_matches) >= 1:
                        parsed['MFG_DATE'] = date_matches[0]
                    if len(date_matches) >= 2:
                        parsed['EXP_DATE'] = date_matches[1]
                
                # Look for lot/batch patterns
                lot_match = re.search(r'[A-Z]{1,3}\d{4,}', data)
                if lot_match:
                    parsed['LOT'] = lot_match.group()
                    
        except Exception as e:
            print(f"GS1 parsing error: {e}")
            
        return parsed
    
    def _detect_with_preprocessing(self, gray_image):
        """Try barcode detection with different preprocessing techniques"""
        results = []
        
        try:
            # Try different preprocessing methods
            preprocessing_methods = [
                lambda img: cv2.threshold(img, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)[1],
                lambda img: cv2.adaptiveThreshold(img, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, 11, 2),
                lambda img: cv2.equalizeHist(img),
                lambda img: cv2.morphologyEx(img, cv2.MORPH_CLOSE, np.ones((3,3), np.uint8))
            ]
            
            for method in preprocessing_methods:
                try:
                    processed = method(gray_image)
                    barcodes = []
                    if PYZBAR_AVAILABLE:
                        barcodes = pyzbar.decode(processed)
                    
                    for barcode in barcodes:
                        barcode_data = barcode.data.decode('utf-8')
                        barcode_type = barcode.type
                        (x, y, w, h) = barcode.rect
                        
                        # Allow all barcodes, including duplicates
                        parsed_data = self._parse_gs1_data(barcode_data)
                        results.append({
                            'type': f"{barcode_type}_PREPROCESSED",
                            'data': barcode_data,
                            'parsed_data': parsed_data,
                            'bbox': (x, y, w, h)
                        })
                except:
                    continue
                    
        except Exception as e:
            print(f"Preprocessing detection error: {e}")
            
        return results
