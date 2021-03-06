# ****************************************************************************
#
# Copyright (c) Microsoft Corporation. 
#
# This source code is subject to terms and conditions of the Microsoft Public License. A 
# copy of the license can be found in the License.html file at the root of this distribution. If 
# you cannot locate the  Microsoft Public License, please send an email to 
# ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
# by the terms of the Microsoft Public License.
#
# You must not remove this notice, or any other, from this software.
#
#
# ****************************************************************************



class ReplBufferStream
  def write_to_repl(str)
    GuiTutorial::Window.current.begin_invoke do
      $repl.output_buffer.write str
    end
  end

  def puts(*args)
    args.each do |arg| 
      write_to_repl arg.to_s
      write_to_repl "\n"
    end
  end
  
  def print (*args)
    args.each {|arg| write_to_repl arg.to_s }
  end
end

# minitest uses Signal.list, which does not exist in Silverlight. So fake it
class Signal
  def self.list() Hash.new end
end

def run_tests()
  $LOAD_PATH << "./Libs/minitest-1.4.2/lib"
  $0 = __FILE__ # minitest expects this to be non-nil
  require "minitest/spec"
  MiniTest::Unit.output = ReplBufferStream.new
  require "test/test_console"
  MiniTest::Unit.new.run(ARGV)
end

Thread.new { run_tests }
