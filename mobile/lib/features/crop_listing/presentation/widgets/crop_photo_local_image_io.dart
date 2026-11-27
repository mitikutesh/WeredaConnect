import 'dart:io';

import 'package:flutter/widgets.dart';

/// [path] as an image decoded at most [cacheWidth] pixels wide (2 GB phones
/// must not decode a 1600 px photo for a 64 dp tile), or null when there is
/// no such file.
ImageProvider? localCropPhotoImage(String? path, {int? cacheWidth}) {
  if (path == null || path.isEmpty) return null;
  final file = File(path);
  if (!file.existsSync()) return null;
  return ResizeImage.resizeIfNeeded(cacheWidth, null, FileImage(file));
}
