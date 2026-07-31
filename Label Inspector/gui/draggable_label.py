from PyQt5.QtWidgets import QLabel
from PyQt5.QtCore import Qt
from PyQt5.QtGui import QCursor

class DraggableLabel(QLabel):
    """A QLabel that forwards mouse events to its parent for dragging functionality"""
    
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.setMouseTracking(True)
    
    def mousePressEvent(self, event):
        """Forward mouse press events to parent safely"""
        if self.parent() and hasattr(self.parent(), 'mousePressEvent'):
            try:
                self.parent().mousePressEvent(event)
            except Exception as e:
                print(f"Warning: mousePressEvent forwarding failed: {e}")
                super().mousePressEvent(event)
        else:
            super().mousePressEvent(event)
    
    def mouseMoveEvent(self, event):
        """Handle mouse move events - don't forward to prevent M1 compatibility issues"""
        # Check if a custom handler was assigned
        if hasattr(self, '_custom_mouse_move'):
            try:
                self._custom_mouse_move(event)
            except Exception as e:
                print(f"Warning: custom mouse move handler failed: {e}")
        
        # Don't forward to parent to avoid M1 compatibility warnings
        super().mouseMoveEvent(event)
    
    def mouseReleaseEvent(self, event):
        """Forward mouse release events to parent safely"""
        if self.parent() and hasattr(self.parent(), 'mouseReleaseEvent'):
            try:
                self.parent().mouseReleaseEvent(event)
            except Exception as e:
                print(f"Warning: mouseReleaseEvent forwarding failed: {e}")
                super().mouseReleaseEvent(event)
        else:
            super().mouseReleaseEvent(event)
    
    def enterEvent(self, event):
        """Handle enter events - don't forward to prevent M1 compatibility issues"""
        super().enterEvent(event)
    
    def leaveEvent(self, event):
        """Handle leave events - don't forward to prevent M1 compatibility issues"""
        super().leaveEvent(event)
