﻿using Xunit;

namespace CsDebugScript.Tests.UI
{
    public class UiTestBase
    {
        public UiTestBase(InteractiveWindowFixture interactiveWindowFixture)
        {
            Window = interactiveWindowFixture.InteractiveWindow;
        }

        public InteractiveWindowWrapper Window { get; private set; }
    }
}
