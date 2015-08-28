﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.ProjectSystem.Designers;
using Microsoft.VisualStudio.ProjectSystem.Designers.Imaging;
using Microsoft.VisualStudio.ProjectSystem.Utilities;

namespace Microsoft.VisualStudio.ProjectSystem.VisualBasic.VS.Designers
{
    /// <summary>
    ///     Provides VIsual Basic project images.
    /// </summary>
    [Export(typeof(IProjectImageMonikerProvider))]
    [AppliesTo(ProjectCapability.VisualBasic)]
    internal class VisualBasicProjectImageMonikerProvider : IProjectImageMonikerProvider
    {
        [ImportingConstructor]
        public VisualBasicProjectImageMonikerProvider()
        {
        }

        public bool TryGetProjectImageMoniker(string key, out ProjectImageMoniker result)
        {
            switch (key)
            {
                case ProjectImageMonikerKey.ProjectRoot:
                    result = KnownMonikers.VBProjectNode.ToProjectSystemType();
                    return true;

                case ProjectImageMonikerKey.AppDesignerFolder:
                    result = KnownMonikers.Property.ToProjectSystemType();
                    return true;

                default:
                    result = default(ProjectImageMoniker);
                    return false;
            }
        }
    }
}
