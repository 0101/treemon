interface CanvasSelectionMetadataContext {
  range: Range;
  rect: {
    left: number;
    top: number;
    right: number;
    bottom: number;
    width: number;
    height: number;
  };
  selectedText: string;
  contextBefore: string;
  contextAfter: string;
  section: string | null;
}

interface Window {
  __canvasSendInstalled?: boolean;
  __canvasSelectionContextInstalled?: boolean;
  __canvasTopLevelTransportAvailable?: boolean;
  canvasMorphInstalled?: boolean;
  canvasSelectionConfig?: {
    includeSurroundingContext?: boolean;
  };
  canvasSelectionMetadata?: (context: CanvasSelectionMetadataContext) => unknown;
  canvasSend?: (action: string, payload?: Record<string, unknown>) => boolean;
}

declare const Idiomorph: {
  morph(root: Element, html: string, options: { morphStyle: "innerHTML" }): void;
};

declare var canvasMorphInternals: unknown;
declare var canvasSelectionContextInternals: unknown;
