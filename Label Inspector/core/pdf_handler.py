import cv2
import numpy as np
import io
from PIL import Image
from PyQt5.QtCore import QObject, pyqtSignal
import fitz  # PyMuPDF

# Try to import pdf2image as fallback
try:
    from pdf2image import convert_from_path
    PDF2IMAGE_AVAILABLE = True
except ImportError:
    PDF2IMAGE_AVAILABLE = False
    print("pdf2image not available, using PyMuPDF only")

class PDFHandler(QObject):
    page_changed = pyqtSignal(int)
    
    def __init__(self):
        super().__init__()
        self.pdf_path = None
        self.current_page = 0
        self.total_pages = 0
        self.zoom_factor = 1.0
        self.current_image = None
        self.pages_cache = {}
        self.pdf_document = None
        
    def load_pdf(self, file_path):
        """Load PDF file using PyMuPDF (primary) or pdf2image (fallback)"""
        try:
            # Close any existing document
            if self.pdf_document:
                self.pdf_document.close()
            
            # Try PyMuPDF first (more reliable on macOS)
            self.pdf_document = fitz.open(file_path)
            self.total_pages = len(self.pdf_document)
            self.pdf_path = file_path
            self.current_page = 0
            self.pages_cache = {}
            
            print(f"Successfully loaded PDF with {self.total_pages} pages using PyMuPDF")
            return True
            
        except Exception as e:
            print(f"PyMuPDF failed: {e}")
            
            # Fallback to pdf2image if available and poppler is installed
            if PDF2IMAGE_AVAILABLE:
                try:
                    test_pages = convert_from_path(file_path, first_page=1, last_page=1, dpi=150)
                    if test_pages:
                        # Get total page count
                        try:
                            import PyPDF2
                            with open(file_path, 'rb') as file:
                                pdf_reader = PyPDF2.PdfReader(file)
                                self.total_pages = len(pdf_reader.pages)
                        except:
                            # Fallback: convert all pages to count them
                            all_pages = convert_from_path(file_path, dpi=150)
                            self.total_pages = len(all_pages)
                        
                        self.pdf_path = file_path
                        self.current_page = 0
                        self.pages_cache = {}
                        print(f"Successfully loaded PDF with {self.total_pages} pages using pdf2image")
                        return True
                except Exception as e2:
                    print(f"pdf2image also failed: {e2}")
            
            print(f"Error loading PDF: {e}")
            return False
            
    def get_current_page_image(self, dpi=300):
        """Get current page as image using PyMuPDF (primary) or pdf2image (fallback)"""
        if not self.pdf_path:
            return None
            
        try:
            # Check cache first
            cache_key = f"{self.current_page}_{dpi}"
            if cache_key in self.pages_cache:
                self.current_image = self.pages_cache[cache_key]
                return self.current_image
            
            # Try PyMuPDF first
            if self.pdf_document:
                try:
                    page = self.pdf_document[self.current_page]
                    
                    # Calculate zoom factor based on desired DPI
                    # PyMuPDF default is 72 DPI - increase significantly for A3 full coverage
                    # For A3 portrait (297x420mm), ensure minimum resolution for full OCR coverage
                    # A3 portrait needs higher zoom to capture full page content
                    page_rect = page.rect
                    if page_rect.height > page_rect.width:  # Portrait orientation
                        zoom = max(dpi / 72.0, 8.0)  # Higher zoom for A3 portrait
                    else:
                        zoom = max(dpi / 72.0, 6.0)  # Standard zoom for landscape
                    mat = fitz.Matrix(zoom, zoom)
                    
                    # Get page dimensions to ensure proper scaling
                    page_rect = page.rect
                    print(f"PDF page dimensions: {page_rect.width} x {page_rect.height}")
                    print(f"Using zoom factor: {zoom} for DPI: {dpi}")
                    
                    # Render page to pixmap
                    pix = page.get_pixmap(matrix=mat)
                    
                    # Convert to PIL Image
                    img_data = pix.tobytes("ppm")
                    pil_image = Image.open(io.BytesIO(img_data))
                    
                    # Convert PIL to numpy array
                    img_array = np.array(pil_image)
                    
                    # Ensure RGB format
                    if len(img_array.shape) == 3:
                        img_rgb = img_array  # PIL already gives RGB
                    else:
                        img_rgb = cv2.cvtColor(img_array, cv2.COLOR_GRAY2RGB)
                    
                    # Cache the result
                    self.pages_cache[cache_key] = img_rgb
                    self.current_image = img_rgb
                    return img_rgb
                    
                except Exception as e:
                    print(f"PyMuPDF page rendering failed: {e}")
            
            # Fallback to pdf2image if PyMuPDF fails
            if PDF2IMAGE_AVAILABLE:
                try:
                    pages = convert_from_path(
                        self.pdf_path, 
                        first_page=self.current_page + 1, 
                        last_page=self.current_page + 1, 
                        dpi=dpi
                    )
                    
                    if pages:
                        pil_image = pages[0]
                        
                        # Convert PIL to numpy array
                        img_array = np.array(pil_image)
                        
                        # Convert RGB to BGR for OpenCV compatibility, then back to RGB for Qt
                        if len(img_array.shape) == 3:
                            img_rgb = img_array  # PIL already gives RGB
                        else:
                            img_rgb = cv2.cvtColor(img_array, cv2.COLOR_GRAY2RGB)
                        
                        # Cache the result
                        self.pages_cache[cache_key] = img_rgb
                        self.current_image = img_rgb
                        return img_rgb
                        
                except Exception as e:
                    print(f"pdf2image page rendering failed: {e}")
            
        except Exception as e:
            print(f"Error getting page image: {e}")
            
        return None
        
    def next_page(self):
        """Go to next page"""
        if self.pdf_document and self.current_page < self.total_pages - 1:
            self.current_page += 1
            self.page_changed.emit(self.current_page)
            return True
        return False
        
    def previous_page(self):
        """Go to previous page"""
        if self.pdf_document and self.current_page > 0:
            self.current_page -= 1
            self.page_changed.emit(self.current_page)
            return True
        return False
        
    def go_to_page(self, page_number):
        """Go to specific page"""
        if self.pdf_document and 0 <= page_number < self.total_pages:
            self.current_page = page_number
            self.page_changed.emit(self.current_page)
            return True
        return False
        
    def get_page_info(self):
        """Get current page information"""
        if self.pdf_document:
            return {
                'current_page': self.current_page + 1,
                'total_pages': self.total_pages,
                'has_next': self.current_page < self.total_pages - 1,
                'has_previous': self.current_page > 0
            }
        return None
        
    def close_pdf(self):
        """Close PDF document"""
        if self.pdf_document:
            self.pdf_document.close()
            self.pdf_document = None
            self.current_page = 0
            self.total_pages = 0
